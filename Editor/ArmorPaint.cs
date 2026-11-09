using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEditor;
using Debug = UnityEngine.Debug;

// Paints the selected object in ArmorPaint and brings the painted textures back.
// Each painted object gets a project at Assets/ArmorPaintProjects/<Name>/<Name>.arm.
// When ArmorPaint closes, textures are exported from the project next to it and
// assigned to the object's material.
[InitializeOnLoad]
public class ArmorPaint {

	public const string ProjectsFolder = "Assets/ArmorPaintProjects";
	const string PendingKey = "ArmorPaintPending";
	static double nextPoll = 0;

	static ArmorPaint() {
		EditorApplication.update += Poll;
		EditorApplication.focusChanged += OnFocusChanged;
	}

	private static string MeshToObj(MeshFilter mf, string name) {
		Mesh m = mf.sharedMesh;

		StringBuilder sb = new StringBuilder();
		sb.Append(string.Format(CultureInfo.InvariantCulture, "o {0}\n", name));

		// Unity is left-handed, OBJ is right-handed: mirror X and reverse the winding order,
		// otherwise ArmorPaint shows a mirrored mesh and strokes appear mirrored back in Unity
		foreach(Vector3 v in m.vertices) {
			sb.Append(string.Format(CultureInfo.InvariantCulture, "v {0} {1} {2}\n", -v.x, v.y, v.z));
		}

		foreach(Vector3 v in m.normals) {
			sb.Append(string.Format(CultureInfo.InvariantCulture, "vn {0} {1} {2}\n", -v.x, v.y, v.z));
		}

		foreach(Vector3 v in m.uv) {
			sb.Append(string.Format(CultureInfo.InvariantCulture, "vt {0} {1}\n", v.x, v.y));
		}

		for (int material = 0; material < m.subMeshCount; material++) {
			int[] triangles = m.GetTriangles(material);
			for (int i = 0; i < triangles.Length; i += 3) {
				sb.Append(string.Format(CultureInfo.InvariantCulture, "f {0}/{0}/{0} {1}/{1}/{1} {2}/{2}/{2}\n",
					triangles[i]+1, triangles[i+2]+1, triangles[i+1]+1));
			}
		}

		return sb.ToString();
	}

	[MenuItem("ArmorPaint/Paint Selected")]
	private static void ArmorPaintPaintOption() {
		GameObject go = Selection.activeGameObject;
		if (go == null) {
			EditorUtility.DisplayDialog("No Selection", "Select a GameObject to paint.", "OK");
			return;
		}

		MeshFilter mf = go.GetComponent<MeshFilter>();
		Renderer renderer = go.GetComponent<Renderer>();
		if (mf == null || mf.sharedMesh == null || renderer == null) {
			EditorUtility.DisplayDialog("No Mesh Filter", "Select a GameObject with Mesh Filter and Mesh Renderer components.", "OK");
			return;
		}

		string exe = GetExecutable();
		if (exe == null) {
			return;
		}

		string name = SanitizeName(go.name);
		string folder = ProjectsFolder + "/" + name;
		string armAsset = folder + "/" + name + ".arm";
		foreach (Pending p in LoadPending()) {
			if (p.arm == armAsset) {
				EditorUtility.DisplayDialog("Already Painting", "'" + name + "' is already open in ArmorPaint.", "OK");
				return;
			}
		}

		EnsureFolder(folder);
		string material = GetOrCreateMaterial(renderer, folder, name);
		string armPath = Path.GetFullPath(armAsset);

		string args;
		if (File.Exists(armPath)) {
			args = Quote(armPath);
		}
		else {
			// New project: open the mesh and save the project to its .arm path right away,
			// so that saving on close goes there without asking for a location
			string objPath = Path.Combine(Path.GetFullPath(EditorPrefs.GetString("ArmorPaintPath")), "data", "temp.obj");
			File.WriteAllText(objPath, MeshToObj(mf, name));

			string scriptPath = Path.GetFullPath("Temp/armorpaint_link.c");
			string armLiteral = armPath.Replace("\\", "\\\\").Replace("\"", "\\\"");
			File.WriteAllText(scriptPath,
				"void main() {\n" +
				"\tproject_filepath_set(\"" + armLiteral + "\");\n" +
				"\tproject_save(0);\n" +
				"}\n");
			args = Quote(Path.GetFullPath(objPath)) + " --script " + Quote(scriptPath);
		}

		Process process = StartArmorPaint(exe, args);
		if (process == null) {
			return;
		}

		List<Pending> pending = LoadPending();
		pending.Add(new Pending { pid = process.Id, arm = armAsset, material = material });
		SavePending(pending);
		Debug.Log("ArmorPaint: painting '" + name + "', textures will be applied to " + material + " when ArmorPaint is closed.");
	}

	[MenuItem("ArmorPaint/Set Path")]
	private static string ArmorPaintPathOption() {
		string path = EditorUtility.OpenFolderPanel("Select folder where ArmorPaint is located", "", "");
		if (!string.IsNullOrEmpty(path)) {
			EditorPrefs.SetString("ArmorPaintPath", path);
		}
		return path;
	}

	private static string GetExecutable() {
		string path = EditorPrefs.HasKey("ArmorPaintPath") ? EditorPrefs.GetString("ArmorPaintPath") : ArmorPaintPathOption();
		if (string.IsNullOrEmpty(path)) {
			return null;
		}
		path = Path.GetFullPath(path);

		string exe;
		if (SystemInfo.operatingSystemFamily == OperatingSystemFamily.Windows) {
			exe = Path.Combine(path, "ArmorPaint.exe");
		}
		else if (SystemInfo.operatingSystemFamily == OperatingSystemFamily.MacOSX) {
			exe = Path.Combine(path, "ArmorPaint.app", "Contents", "MacOS", "ArmorPaint");
		}
		else {
			exe = Path.Combine(path, "ArmorPaint");
		}

		if (!File.Exists(exe)) {
			EditorUtility.DisplayDialog("ArmorPaint Not Found", "ArmorPaint was not found at:\n" + exe + "\n\nUse ArmorPaint - Set Path to select the folder where ArmorPaint is located.", "OK");
			return null;
		}
		return exe;
	}

	private static Process StartArmorPaint(string exe, string args) {
		ProcessStartInfo info = new ProcessStartInfo(exe, args);
		info.UseShellExecute = false;
		info.WorkingDirectory = Path.GetDirectoryName(exe);
		try {
			return Process.Start(info);
		}
		catch (Exception e) {
			Debug.LogError("ArmorPaint: failed to start " + exe + ": " + e.Message);
			return null;
		}
	}

	// Reuses the renderer's material when it is an editable asset, otherwise creates a copy next to the project
	private static string GetOrCreateMaterial(Renderer renderer, string folder, string name) {
		Material current = renderer.sharedMaterial;
		string currentPath = current != null ? AssetDatabase.GetAssetPath(current) : "";
		if (currentPath.StartsWith("Assets/") && currentPath.EndsWith(".mat")) {
			return currentPath;
		}

		Material material;
		if (current != null) {
			material = new Material(current);
		}
		else {
			Shader shader = Shader.Find("Universal Render Pipeline/Lit");
			if (shader == null) shader = Shader.Find("HDRP/Lit");
			if (shader == null) shader = Shader.Find("Standard");
			material = new Material(shader);
		}

		string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + name + ".mat");
		AssetDatabase.CreateAsset(material, path);

		Undo.RecordObject(renderer, "Assign ArmorPaint Material");
		Material[] materials = renderer.sharedMaterials;
		if (materials.Length == 0) {
			materials = new Material[1];
		}
		materials[0] = material;
		renderer.sharedMaterials = materials;
		PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
		return path;
	}

	// Waits for ArmorPaint instances started by Paint Selected to close
	private static void Poll() {
		if (EditorApplication.timeSinceStartup < nextPoll) {
			return;
		}
		nextPoll = EditorApplication.timeSinceStartup + 0.5;

		List<Pending> pending = LoadPending();
		if (pending.Count == 0) {
			return;
		}

		List<Pending> closed = pending.FindAll(p => !IsRunning(p.pid));
		if (closed.Count == 0) {
			return;
		}
		pending.RemoveAll(p => closed.Contains(p));
		SavePending(pending);

		foreach (Pending p in closed) {
			if (ExportTextures(p.arm)) {
				ApplyTextures(p.arm, p.material);
			}
		}
	}

	// Picks up projects saved while Unity was not tracking ArmorPaint, such as after Unity was restarted
	private static void OnFocusChanged(bool focused) {
		if (!focused || !Directory.Exists(ProjectsFolder)) {
			return;
		}
		List<Pending> pending = LoadPending();
		foreach (string file in Directory.GetFiles(ProjectsFolder, "*.arm", SearchOption.AllDirectories)) {
			string arm = file.Replace("\\", "/");
			if (pending.Exists(p => p.arm == arm)) {
				continue;
			}
			string baseTexture = TexturePath(arm, "base");
			if (File.Exists(baseTexture) && File.GetLastWriteTimeUtc(arm) > File.GetLastWriteTimeUtc(baseTexture)) {
				ExportTextures(arm);
			}
		}
	}

	private static bool ExportTextures(string armAsset) {
		string armPath = Path.GetFullPath(armAsset);
		if (!File.Exists(armPath)) {
			Debug.Log("ArmorPaint: " + armAsset + " was not saved, no textures to apply.");
			return false;
		}
		string exe = GetExecutable();
		if (exe == null) {
			return false;
		}

		string dir = Path.GetDirectoryName(armPath);
		string args = Quote(armPath) + " --export-textures png unity " + Quote(dir) + " --background";
		EditorUtility.DisplayProgressBar("ArmorPaint", "Exporting textures from " + Path.GetFileName(armPath), 0.5f);
		try {
			Process process = StartArmorPaint(exe, args);
			if (process == null) {
				return false;
			}
			if (!process.WaitForExit(120000)) {
				Debug.LogError("ArmorPaint: texture export from " + armAsset + " timed out.");
				return false;
			}
		}
		finally {
			EditorUtility.ClearProgressBar();
		}

		if (!File.Exists(TexturePath(armAsset, "base"))) {
			Debug.LogError("ArmorPaint: texture export from " + armAsset + " failed.");
			return false;
		}
		foreach (string type in new string[] { "base", "nor", "mos", "height" }) {
			if (File.Exists(TexturePath(armAsset, type))) {
				AssetDatabase.ImportAsset(TexturePath(armAsset, type), ImportAssetOptions.ForceUpdate);
			}
		}
		Debug.Log("ArmorPaint: textures exported from " + armAsset);
		return true;
	}

	private static void ApplyTextures(string armAsset, string materialPath) {
		Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
		if (material == null) {
			Debug.LogWarning("ArmorPaint: material " + materialPath + " not found, textures were not assigned.");
			return;
		}

		Texture2D baseColor = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath(armAsset, "base"));
		Texture2D normal = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath(armAsset, "nor"));
		Texture2D mos = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath(armAsset, "mos"));

		Undo.RecordObject(material, "Apply ArmorPaint Textures");

		// Property names cover Built-in Standard, URP Lit and HDRP Lit
		if (baseColor != null) {
			SetTexture(material, baseColor, "_BaseMap", "_MainTex", "_BaseColorMap");
			SetColor(material, Color.white, "_BaseColor", "_Color");
		}
		if (normal != null) {
			SetTexture(material, normal, "_BumpMap", "_NormalMap");
			material.EnableKeyword("_NORMALMAP");
		}
		if (mos != null) {
			// mos: R metallic, G occlusion, A smoothness
			SetTexture(material, mos, "_MetallicGlossMap", "_OcclusionMap", "_MaskMap");
			SetFloat(material, 1.0f, "_Smoothness", "_GlossMapScale", "_OcclusionStrength");
			SetFloat(material, 0.0f, "_SmoothnessTextureChannel");
			material.EnableKeyword("_METALLICGLOSSMAP");
			material.EnableKeyword("_METALLICSPECGLOSSMAP");
			material.EnableKeyword("_OCCLUSIONMAP");
			material.EnableKeyword("_MASKMAP");
		}

		EditorUtility.SetDirty(material);
		AssetDatabase.SaveAssets();
		Debug.Log("ArmorPaint: textures applied to " + materialPath);
	}

	private static void SetTexture(Material material, Texture texture, params string[] names) {
		foreach (string name in names) {
			if (material.HasProperty(name)) material.SetTexture(name, texture);
		}
	}

	private static void SetColor(Material material, Color color, params string[] names) {
		foreach (string name in names) {
			if (material.HasProperty(name)) material.SetColor(name, color);
		}
	}

	private static void SetFloat(Material material, float value, params string[] names) {
		foreach (string name in names) {
			if (material.HasProperty(name)) material.SetFloat(name, value);
		}
	}

	// ArmorPaint names exported textures <project>_<type>.png
	public static string TexturePath(string armAsset, string type) {
		return armAsset.Substring(0, armAsset.Length - 4) + "_" + type + ".png";
	}

	private static void EnsureFolder(string folder) {
		string[] parts = folder.Split('/');
		string path = parts[0];
		for (int i = 1; i < parts.Length; i++) {
			string next = path + "/" + parts[i];
			if (!AssetDatabase.IsValidFolder(next)) {
				AssetDatabase.CreateFolder(path, parts[i]);
			}
			path = next;
		}
	}

	private static string SanitizeName(string name) {
		foreach (char c in Path.GetInvalidFileNameChars()) {
			name = name.Replace(c, '_');
		}
		name = name.Trim().TrimEnd('.');
		return name.Length > 0 ? name : "Object";
	}

	private static string Quote(string s) {
		return "\"" + s + "\"";
	}

	private static bool IsRunning(int pid) {
		try {
			return !Process.GetProcessById(pid).HasExited;
		}
		catch (Exception) {
			return false;
		}
	}

	// Running ArmorPaint instances are kept in SessionState to survive script reloads
	private class Pending {
		public int pid;
		public string arm;
		public string material;
	}

	private static List<Pending> LoadPending() {
		List<Pending> list = new List<Pending>();
		foreach (string line in SessionState.GetString(PendingKey, "").Split('\n')) {
			string[] parts = line.Split('|');
			int pid;
			if (parts.Length == 3 && int.TryParse(parts[0], out pid)) {
				list.Add(new Pending { pid = pid, arm = parts[1], material = parts[2] });
			}
		}
		return list;
	}

	private static void SavePending(List<Pending> list) {
		StringBuilder sb = new StringBuilder();
		foreach (Pending p in list) {
			sb.Append(p.pid).Append('|').Append(p.arm).Append('|').Append(p.material).Append('\n');
		}
		SessionState.SetString(PendingKey, sb.ToString());
	}
}

// Sets up import settings for textures exported by ArmorPaint
public class ArmorPaintTexturePostprocessor : AssetPostprocessor {
	void OnPreprocessTexture() {
		if (!assetPath.StartsWith(ArmorPaint.ProjectsFolder + "/") || !assetImporter.importSettingsMissing) {
			return;
		}
		TextureImporter importer = (TextureImporter)assetImporter;
		if (assetPath.EndsWith("_nor.png")) {
			importer.textureType = TextureImporterType.NormalMap;
		}
		else if (assetPath.EndsWith("_mos.png") || assetPath.EndsWith("_height.png")) {
			importer.sRGBTexture = false;
		}
	}
}
