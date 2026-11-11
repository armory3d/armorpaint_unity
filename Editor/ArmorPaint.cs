using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEditor;
using Debug = UnityEngine.Debug;

// Paints the selected objects in ArmorPaint and brings the painted textures back.
// The selection gets a project at Assets/ArmorPaintProjects/<Name>/<Name>.arm, multiple selected
// objects are painted together in one project and share one material.
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

	// A selected object to paint
	private class Target {
		public string name;
		public MeshFilter filter;
		public Renderer renderer;
	}

	// Writes all targets into one OBJ, one object per target.
	// A single object is exported in its local space. Multiple objects are exported with their
	// world transforms, centered around the selection, so they are laid out in ArmorPaint as in the scene.
	private static string MeshesToObj(List<Target> targets) {
		// Normals are written only when every mesh has them, otherwise ArmorPaint computes them
		bool hasNormals = targets.TrueForAll(t => t.filter.sharedMesh.normals.Length == t.filter.sharedMesh.vertexCount);

		Vector3 center = Vector3.zero;
		if (targets.Count > 1) {
			Bounds bounds = targets[0].renderer.bounds;
			foreach (Target t in targets) {
				bounds.Encapsulate(t.renderer.bounds);
			}
			center = bounds.center;
		}

		StringBuilder sb = new StringBuilder();
		// OBJ indices are global to the file, each object continues after the previous one
		int indexBase = 0;
		foreach (Target t in targets) {
			Mesh m = t.filter.sharedMesh;
			sb.Append(string.Format(CultureInfo.InvariantCulture, "o {0}\n", t.name));

			Matrix4x4 matrix = Matrix4x4.identity;
			if (targets.Count > 1) {
				matrix = Matrix4x4.Translate(-center) * t.filter.transform.localToWorldMatrix;
			}
			Matrix4x4 normalMatrix = matrix.inverse.transpose;
			// Mirroring transforms flip the winding order
			bool flip = matrix.determinant < 0.0f;

			// Unity is left-handed, OBJ is right-handed: mirror X and reverse the winding order,
			// otherwise ArmorPaint shows a mirrored mesh and strokes appear mirrored back in Unity
			Vector3[] vertices = m.vertices;
			foreach (Vector3 vertex in vertices) {
				Vector3 v = matrix.MultiplyPoint3x4(vertex);
				sb.Append(string.Format(CultureInfo.InvariantCulture, "v {0} {1} {2}\n", -v.x, v.y, v.z));
			}

			if (hasNormals) {
				foreach (Vector3 normal in m.normals) {
					Vector3 v = normalMatrix.MultiplyVector(normal).normalized;
					sb.Append(string.Format(CultureInfo.InvariantCulture, "vn {0} {1} {2}\n", -v.x, v.y, v.z));
				}
			}

			foreach (Vector2 v in m.uv) {
				sb.Append(string.Format(CultureInfo.InvariantCulture, "vt {0} {1}\n", v.x, v.y));
			}

			string face = hasNormals ? "f {0}/{0}/{0} {1}/{1}/{1} {2}/{2}/{2}\n" : "f {0}/{0} {1}/{1} {2}/{2}\n";
			for (int material = 0; material < m.subMeshCount; material++) {
				int[] triangles = m.GetTriangles(material);
				for (int i = 0; i < triangles.Length; i += 3) {
					int a = indexBase + triangles[i] + 1;
					int b = indexBase + triangles[i + 1] + 1;
					int c = indexBase + triangles[i + 2] + 1;
					if (flip) {
						sb.Append(string.Format(CultureInfo.InvariantCulture, face, a, b, c));
					}
					else {
						sb.Append(string.Format(CultureInfo.InvariantCulture, face, a, c, b));
					}
				}
			}
			indexBase += vertices.Length;
		}

		return sb.ToString();
	}

	private static bool IsPaintable(MeshFilter mf) {
		return mf.sharedMesh != null && mf.GetComponent<Renderer>() != null;
	}

	// Every selected GameObject with a mesh is painted, including the meshes of its children,
	// so that selecting a group paints all of its objects.
	// The project names come from the painted meshes only, not from how they were selected:
	// each mesh is named after its highest ancestor whose meshes are all painted. Selecting a group
	// or selecting all of its meshes one by one both map to the group, and so to the same project.
	private static List<Target> GetTargets(Transform[] selected, out List<string> names) {
		List<Transform> roots = new List<Transform>(selected);
		roots.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

		List<Target> targets = new List<Target>();
		HashSet<MeshFilter> painted = new HashSet<MeshFilter>();
		HashSet<string> objectNames = new HashSet<string>();
		foreach (Transform root in roots) {
			foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>()) {
				if (!IsPaintable(mf) || !painted.Add(mf)) {
					continue;
				}
				// Object names in the OBJ are kept unique, children of a group often share names
				string objectName = SanitizeName(mf.name);
				for (int i = 2; !objectNames.Add(objectName); i++) {
					objectName = SanitizeName(mf.name) + "_" + i;
				}
				targets.Add(new Target { name = objectName, filter = mf, renderer = mf.GetComponent<Renderer>() });
			}
		}

		Dictionary<Transform, bool> coveredCache = new Dictionary<Transform, bool>();
		Func<Transform, bool> covered = t => {
			bool result;
			if (!coveredCache.TryGetValue(t, out result)) {
				result = Array.TrueForAll(t.GetComponentsInChildren<MeshFilter>(), mf => !IsPaintable(mf) || painted.Contains(mf));
				coveredCache[t] = result;
			}
			return result;
		};
		HashSet<Transform> groups = new HashSet<Transform>();
		foreach (Target t in targets) {
			Transform group = t.filter.transform;
			while (group.parent != null && covered(group.parent)) {
				group = group.parent;
			}
			groups.Add(group);
		}
		names = new List<string>();
		foreach (Transform group in groups) {
			names.Add(SanitizeName(group.name));
		}
		// Sorted so that the same meshes map to the same project regardless of selection order
		names.Sort(string.CompareOrdinal);
		return targets;
	}

	[MenuItem("ArmorPaint/Paint Selected")]
	private static void ArmorPaintPaintOption() {
		List<string> names;
		List<Target> targets = GetTargets(Selection.GetTransforms(SelectionMode.TopLevel), out names);
		if (targets.Count == 0) {
			EditorUtility.DisplayDialog("No Mesh Filter", "Select one or more GameObjects with Mesh Filter and Mesh Renderer components.", "OK");
			return;
		}
		foreach (Target t in targets) {
			Mesh m = t.filter.sharedMesh;
			if (m.uv.Length != m.vertexCount) {
				EditorUtility.DisplayDialog("No UVs", "'" + m.name + "' has no UVs, it can not be painted.", "OK");
				return;
			}
		}

		string exe = GetExecutable();
		if (exe == null) {
			return;
		}

		string name = ProjectName(names);
		string folder = ProjectsFolder + "/" + name;
		string armAsset = folder + "/" + name + ".arm";
		foreach (Pending p in LoadPending()) {
			if (p.arm == armAsset) {
				EditorUtility.DisplayDialog("Already Painting", "'" + name + "' is already open in ArmorPaint.", "OK");
				return;
			}
		}

		EnsureFolder(folder);
		string material = targets.Count == 1 ?
			GetOrCreateMaterial(targets[0].renderer, folder, name) :
			GetOrCreateSharedMaterial(targets, folder, name);
		string armPath = Path.GetFullPath(armAsset);

		string args;
		if (File.Exists(armPath)) {
			args = Quote(armPath);
		}
		else {
			// New project: open the mesh and save the project to its .arm path right away,
			// so that saving on close goes there without asking for a location
			string objPath = Path.Combine(Path.GetFullPath(EditorPrefs.GetString("ArmorPaintPath")), "data", "temp.obj");
			File.WriteAllText(objPath, MeshesToObj(targets));

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
		Debug.Log("ArmorPaint: painting '" + name + "' (" + targets.Count + " objects), textures will be applied to " + material + " when ArmorPaint is closed.");
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

	// Multiple objects share one material in the project folder, created from the first object's material,
	// and assigned to all material slots of every object: ArmorPaint paints each mesh as a whole
	private static string GetOrCreateSharedMaterial(List<Target> targets, string folder, string name) {
		string path = folder + "/" + name + ".mat";
		Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
		if (material == null) {
			Material current = targets[0].renderer.sharedMaterial;
			if (current != null) {
				material = new Material(current);
			}
			else {
				Shader shader = Shader.Find("Universal Render Pipeline/Lit");
				if (shader == null) shader = Shader.Find("HDRP/Lit");
				if (shader == null) shader = Shader.Find("Standard");
				material = new Material(shader);
			}
			AssetDatabase.CreateAsset(material, path);
		}

		Undo.SetCurrentGroupName("Assign ArmorPaint Material");
		foreach (Target t in targets) {
			Material[] materials = t.renderer.sharedMaterials;
			if (materials.Length == 0) {
				materials = new Material[1];
			}
			if (Array.TrueForAll(materials, m => m == material)) {
				continue;
			}
			Undo.RecordObject(t.renderer, "Assign ArmorPaint Material");
			for (int i = 0; i < materials.Length; i++) {
				materials[i] = material;
			}
			t.renderer.sharedMaterials = materials;
			PrefabUtility.RecordPrefabInstancePropertyModifications(t.renderer);
		}
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

	// Project name of the painted objects, sorted by name. Multiple objects share one project,
	// named after all of them, or after the first one and a hash when that gets too long.
	private static string ProjectName(List<string> names) {
		const int maxLength = 48;
		string joined = string.Join("_", names);
		if (names.Count == 1 || joined.Length <= maxLength) {
			return joined;
		}
		// FNV-1a, string.GetHashCode is not stable across sessions
		uint hash = 2166136261;
		foreach (char c in string.Join("/", names)) {
			hash = (hash ^ c) * 16777619;
		}
		string first = names[0].Length > maxLength - 18 ? names[0].Substring(0, maxLength - 18) : names[0];
		return string.Format(CultureInfo.InvariantCulture, "{0}_{1}_{2:x8}", first, names.Count, hash);
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
