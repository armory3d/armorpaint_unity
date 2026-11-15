using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using Debug = UnityEngine.Debug;

// Paints the selected objects in ArmorPaint and brings the painted textures back.
// The selection gets a project at Assets/ArmorPaintProjects/<Name>/<Name>.arm, starting from the objects'
// current materials. Objects painted together share one material, their UV maps should not overlap.
// When ArmorPaint closes, textures are exported from the project next to it and
// assigned to the project material. Unpaint Selected restores the original materials and deletes the project.
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

	[MenuItem("ArmorPaint/Paint Selected", false, 0)]
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
		string armPath = Path.GetFullPath(armAsset);

		string args;
		if (File.Exists(armPath)) {
			args = Quote(armPath);
		}
		else {
			string objPath = Path.Combine(Path.GetFullPath(EditorPrefs.GetString("ArmorPaintPath")), "data", "temp.obj");
			File.WriteAllText(objPath, MeshesToObj(targets));

			Directory.CreateDirectory("Temp/ArmorPaint");
			Dictionary<string, string> baked = BakeTextures(targets, Path.GetFullPath("Temp/ArmorPaint/" + name));

			string scriptPath = Path.GetFullPath("Temp/armorpaint_link.c");
			File.WriteAllText(scriptPath, LinkScript(armPath, baked));
			args = Quote(Path.GetFullPath(objPath)) + " --script " + Quote(scriptPath);
		}

		string material = AssignProjectMaterial(targets, folder, name);

		Process process = StartArmorPaint(exe, args);
		if (process == null) {
			return;
		}

		List<Pending> pending = LoadPending();
		pending.Add(new Pending { pid = process.Id, arm = armAsset, material = material });
		SavePending(pending);
		Debug.Log("ArmorPaint: painting '" + name + "' (" + targets.Count + " objects), textures will be applied to " + material + " when ArmorPaint is closed.");
	}

	[MenuItem("ArmorPaint/Set Path", false, 12)]
	private static string ArmorPaintPathOption() {
		string path = EditorUtility.OpenFolderPanel("Select folder where ArmorPaint is located", "", "");
		if (!string.IsNullOrEmpty(path)) {
			EditorPrefs.SetString("ArmorPaintPath", path);
		}
		return path;
	}

	const string BakeSizeKey = "ArmorPaintBakeSize";
	static int BakeSize { get { return EditorPrefs.GetInt(BakeSizeKey, 2048); } }

	private static bool BakeSizeCheck(int size) {
		Menu.SetChecked("ArmorPaint/Bake Resolution/" + size, BakeSize == size);
		return true;
	}

	[MenuItem("ArmorPaint/Bake Resolution/512", false, 13)] private static void Bake512() { EditorPrefs.SetInt(BakeSizeKey, 512); }
	[MenuItem("ArmorPaint/Bake Resolution/1024", false, 14)] private static void Bake1024() { EditorPrefs.SetInt(BakeSizeKey, 1024); }
	[MenuItem("ArmorPaint/Bake Resolution/2048", false, 15)] private static void Bake2048() { EditorPrefs.SetInt(BakeSizeKey, 2048); }
	[MenuItem("ArmorPaint/Bake Resolution/4096", false, 16)] private static void Bake4096() { EditorPrefs.SetInt(BakeSizeKey, 4096); }
	[MenuItem("ArmorPaint/Bake Resolution/8192", false, 17)] private static void Bake8192() { EditorPrefs.SetInt(BakeSizeKey, 8192); }
	[MenuItem("ArmorPaint/Bake Resolution/512", true)] private static bool Bake512Check() { return BakeSizeCheck(512); }
	[MenuItem("ArmorPaint/Bake Resolution/1024", true)] private static bool Bake1024Check() { return BakeSizeCheck(1024); }
	[MenuItem("ArmorPaint/Bake Resolution/2048", true)] private static bool Bake2048Check() { return BakeSizeCheck(2048); }
	[MenuItem("ArmorPaint/Bake Resolution/4096", true)] private static bool Bake4096Check() { return BakeSizeCheck(4096); }
	[MenuItem("ArmorPaint/Bake Resolution/8192", true)] private static bool Bake8192Check() { return BakeSizeCheck(8192); }

	// Bakes the current materials of the targets into one texture set in their shared UV map, <prefix>_<type>.png
	private static Dictionary<string, string> BakeTextures(List<Target> targets, string prefix) {
		Dictionary<string, string> paths = new Dictionary<string, string>();
		Shader shader = Shader.Find("Hidden/ArmorPaint/Bake");
		if (shader == null) {
			Debug.LogWarning("ArmorPaint: bake shader not found, painting starts from an empty layer.");
			return paths;
		}
		int size = BakeSize;
		Material bake = new Material(shader);
		RenderTexture[] rts = {
			RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB),
			RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear),
			RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear),
		};
		string[] types = { "base", "orm", "nor" };
		try {
			CommandBuffer cmd = new CommandBuffer();
			RenderTargetIdentifier[] colors = { rts[0], rts[1], rts[2] };
			cmd.SetRenderTarget(colors, colors[0]);
			cmd.ClearRenderTarget(false, true, Color.clear);
			foreach (Target t in targets) {
				Mesh mesh = t.filter.sharedMesh;
				Material[] materials = t.renderer.sharedMaterials;
				for (int i = 0; i < mesh.subMeshCount && i < materials.Length; i++) {
					if (materials[i] != null) {
						cmd.DrawMesh(mesh, Matrix4x4.identity, bake, i, 0, BakeProperties(materials[i]));
					}
				}
			}
			Graphics.ExecuteCommandBuffer(cmd);
			cmd.Release();

			Color32[][] maps = new Color32[3][];
			for (int i = 0; i < 3; i++) {
				maps[i] = ReadPixels(rts[i], i > 0);
			}
			if (Array.TrueForAll(maps[0], c => c.a == 0)) {
				Debug.LogWarning("ArmorPaint: nothing was baked, painting starts from an empty layer.");
				return paths;
			}
			Dilate(maps, size, 16);

			for (int i = 0; i < 3; i++) {
				Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false, i > 0);
				tex.SetPixels32(maps[i]);
				string path = prefix + "_" + types[i] + ".png";
				File.WriteAllBytes(path, tex.EncodeToPNG());
				UnityEngine.Object.DestroyImmediate(tex);
				paths[types[i]] = path;
			}
			Debug.Log("ArmorPaint: baked materials of " + targets.Count + " objects");
		}
		finally {
			foreach (RenderTexture rt in rts) {
				RenderTexture.ReleaseTemporary(rt);
			}
			UnityEngine.Object.DestroyImmediate(bake);
		}
		return paths;
	}

	// Property names cover Built-in Standard, URP Lit and HDRP Lit
	private static MaterialPropertyBlock BakeProperties(Material m) {
		MaterialPropertyBlock p = new MaterialPropertyBlock();
		string baseName = Array.Find(new string[] { "_BaseColorMap", "_BaseMap", "_MainTex" }, m.HasProperty);
		if (baseName != null) {
			Vector2 scale = m.GetTextureScale(baseName);
			Vector2 offset = m.GetTextureOffset(baseName);
			p.SetVector("_ApST", new Vector4(scale.x, scale.y, offset.x, offset.y));
		}
		else {
			p.SetVector("_ApST", new Vector4(1, 1, 0, 0));
		}
		p.SetTexture("_ApBase", GetTexture(m, baseName) ?? Texture2D.whiteTexture);
		p.SetColor("_ApColor", m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white);

		Texture normal = GetTexture(m, "_NormalMap") ?? GetTexture(m, "_BumpMap");
		p.SetFloat("_ApHasNormal", normal != null ? 1 : 0);
		p.SetTexture("_ApNormal", normal ?? Texture2D.normalTexture);
		p.SetFloat("_ApNormalScale", GetFloat(m, 1, "_NormalScale", "_BumpScale"));

		float metallic = GetFloat(m, 0, "_Metallic");
		float smoothness = GetFloat(m, 0.5f, "_Smoothness", "_Glossiness");
		Texture mask = GetTexture(m, "_MaskMap");
		Texture metallicMap = GetTexture(m, "_MetallicGlossMap");
		bool smoothFromAlbedo = mask == null && GetFloat(m, 0, "_SmoothnessTextureChannel") > 0.5f;
		p.SetFloat("_ApSmoothFromAlbedo", smoothFromAlbedo ? 1 : 0);
		if (mask != null) {
			// HDRP mask: R metallic, G occlusion, A smoothness, each remapped
			p.SetTexture("_ApMetal", mask);
			p.SetVector("_ApRemap", new Vector4(GetFloat(m, 0, "_MetallicRemapMin"), GetFloat(m, 1, "_MetallicRemapMax"), GetFloat(m, 0, "_SmoothnessRemapMin"), GetFloat(m, 1, "_SmoothnessRemapMax")));
			p.SetTexture("_ApOcclusion", mask);
			p.SetVector("_ApOcclusionRange", new Vector4(GetFloat(m, 0, "_AORemapMin"), GetFloat(m, 1, "_AORemapMax"), 1, 0));
			return p;
		}
		if (metallicMap != null) {
			// R metallic, A smoothness scaled by _GlossMapScale in Built-in Standard, _Smoothness in URP Lit
			float scale = m.HasProperty("_Glossiness") ? GetFloat(m, 1, "_GlossMapScale") : smoothness;
			p.SetTexture("_ApMetal", metallicMap);
			p.SetVector("_ApRemap", new Vector4(0, 1, 0, scale));
		}
		else {
			p.SetTexture("_ApMetal", Texture2D.whiteTexture);
			p.SetVector("_ApRemap", new Vector4(metallic, metallic, smoothFromAlbedo ? 0 : smoothness, smoothness));
		}
		p.SetTexture("_ApOcclusion", GetTexture(m, "_OcclusionMap") ?? Texture2D.whiteTexture);
		p.SetVector("_ApOcclusionRange", new Vector4(0, 1, GetFloat(m, 1, "_OcclusionStrength"), 0));
		return p;
	}

	private static Texture GetTexture(Material m, string name) {
		return name != null && m.HasProperty(name) ? m.GetTexture(name) : null;
	}

	private static float GetFloat(Material m, float value, params string[] names) {
		string name = Array.Find(names, m.HasProperty);
		return name != null ? m.GetFloat(name) : value;
	}

	private static Color32[] ReadPixels(RenderTexture rt, bool linear) {
		RenderTexture active = RenderTexture.active;
		RenderTexture.active = rt;
		Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, linear);
		tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
		RenderTexture.active = active;
		Color32[] pixels = tex.GetPixels32();
		UnityEngine.Object.DestroyImmediate(tex);
		return pixels;
	}

	// Extends the UV islands into the background so that texture filtering at their borders does not pick it up
	private static void Dilate(Color32[][] maps, int size, int iterations) {
		bool[] covered = new bool[size * size];
		for (int i = 0; i < covered.Length; i++) {
			covered[i] = maps[0][i].a > 0;
		}
		int[] dx = { -1, 1, 0, 0 };
		int[] dy = { 0, 0, -1, 1 };
		bool[] queued = new bool[size * size];
		List<int> frontier = new List<int>();
		for (int i = 0; i < covered.Length; i++) {
			if (covered[i]) {
				continue;
			}
			int x = i % size, y = i / size;
			for (int k = 0; k < 4; k++) {
				int nx = x + dx[k], ny = y + dy[k];
				if (nx >= 0 && ny >= 0 && nx < size && ny < size && covered[ny * size + nx]) {
					frontier.Add(i);
					queued[i] = true;
					break;
				}
			}
		}
		for (int it = 0; it < iterations && frontier.Count > 0; it++) {
			foreach (Color32[] map in maps) {
				Color32[] values = new Color32[frontier.Count];
				for (int f = 0; f < frontier.Count; f++) {
					int i = frontier[f], x = i % size, y = i / size, n = 0, r = 0, g = 0, b = 0;
					for (int k = 0; k < 4; k++) {
						int nx = x + dx[k], ny = y + dy[k];
						if (nx >= 0 && ny >= 0 && nx < size && ny < size && covered[ny * size + nx]) {
							Color32 c = map[ny * size + nx];
							r += c.r; g += c.g; b += c.b; n++;
						}
					}
					values[f] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
				}
				for (int f = 0; f < frontier.Count; f++) {
					map[frontier[f]] = values[f];
				}
			}
			List<int> next = new List<int>();
			foreach (int i in frontier) {
				covered[i] = true;
			}
			foreach (int i in frontier) {
				int x = i % size, y = i / size;
				for (int k = 0; k < 4; k++) {
					int nx = x + dx[k], ny = y + dy[k], ni = ny * size + nx;
					if (nx >= 0 && ny >= 0 && nx < size && ny < size && !covered[ni] && !queued[ni]) {
						queued[ni] = true;
						next.Add(ni);
					}
				}
			}
			frontier = next;
		}
		foreach (Color32[] map in maps) {
			for (int i = 0; i < map.Length; i++) {
				map[i].a = 255;
			}
		}
	}

	// Fills the first layer of a new project with the baked textures and saves it to its .arm path,
	// so that saving on close goes there without asking for a location
	private static string LinkScript(string armPath, Dictionary<string, string> baked) {
		StringBuilder sb = new StringBuilder();
		if (baked.Count > 0) {
			sb.Append("int assets = 0;\n");
			sb.Append("int import_texture(char *path) {\n\tscript_import_asset(path, false);\n\tassets = assets + 1;\n\treturn assets - 1;\n}\n\n");
		}
		sb.Append("void main() {\n");
		if (baked.Count > 0) {
			sb.Append("\tslot_material_t *unity = script_material_create(\"Unity\");\n");
			sb.Append("\tui_node_t *out = script_material_get_node(\"OUTPUT_MATERIAL_PBR\");\n");
			sb.Append("\tui_node_t *tex;\n");
			// Color space button of the image node: 0 auto, 1 linear, 2 sRGB, 3 DirectX normal map
			// Output sockets: 0 base color, 2 occlusion, 3 roughness, 4 metallic, 5 normal map
			sb.Append("\ttex = script_material_create_node_at(\"TEX_IMAGE\", 72, 100);\n");
			sb.Append("\tscript_material_set_button(tex, 0, import_texture(" + CLiteral(baked["base"]) + "));\n");
			sb.Append("\tscript_material_connect(tex, 0, out, 0);\n");
			sb.Append("\ttex = script_material_create_node_at(\"TEX_IMAGE\", 72, 280);\n");
			sb.Append("\tscript_material_set_button(tex, 0, import_texture(" + CLiteral(baked["orm"]) + "));\n");
			sb.Append("\tscript_material_set_button(tex, 1, 1);\n");
			sb.Append("\tui_node_t *sep = script_material_create_node_at(\"SEPARATE_COLOR\", 300, 280);\n");
			sb.Append("\tscript_material_connect(tex, 0, sep, 0);\n");
			sb.Append("\tscript_material_connect(sep, 0, out, 2);\n");
			sb.Append("\tscript_material_connect(sep, 1, out, 3);\n");
			sb.Append("\tscript_material_connect(sep, 2, out, 4);\n");
			sb.Append("\ttex = script_material_create_node_at(\"TEX_IMAGE\", 72, 460);\n");
			sb.Append("\tscript_material_set_button(tex, 0, import_texture(" + CLiteral(baked["nor"]) + "));\n");
			sb.Append("\tscript_material_set_button(tex, 1, 1);\n");
			sb.Append("\tscript_material_connect(tex, 0, out, 5);\n");
			sb.Append("\tscript_material_update();\n");
			sb.Append("\tscript_fill_layer();\n");
			sb.Append("\tscript_material_delete(unity);\n");
		}
		sb.Append("\tproject_filepath_set(" + CLiteral(armPath) + ");\n");
		sb.Append("\tproject_save(0);\n}\n");
		return sb.ToString();
	}

	private static string CLiteral(string s) {
		return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
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

	// The painted objects share one material in the project folder, created from the first object's material.
	// The replaced materials are recorded for Unpaint.
	private static string AssignProjectMaterial(List<Target> targets, string folder, string name) {
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

		Originals originals = LoadOriginals(folder);
		Undo.SetCurrentGroupName("Assign ArmorPaint Material");
		foreach (Target t in targets) {
			Material[] materials = t.renderer.sharedMaterials;
			if (materials.Length > 0 && Array.TrueForAll(materials, m => m == material)) {
				continue;
			}
			string id = ObjectId(t.renderer);
			originals.renderers.RemoveAll(o => o.renderer == id);
			originals.renderers.Add(new OriginalMaterials { renderer = id, materials = Array.ConvertAll(materials, ObjectId) });

			Undo.RecordObject(t.renderer, "Assign ArmorPaint Material");
			Material[] assigned = new Material[Math.Max(materials.Length, 1)];
			for (int i = 0; i < assigned.Length; i++) {
				assigned[i] = material;
			}
			t.renderer.sharedMaterials = assigned;
			PrefabUtility.RecordPrefabInstancePropertyModifications(t.renderer);
		}
		File.WriteAllText(OriginalsPath(folder), JsonUtility.ToJson(originals, true));
		return path;
	}

	[Serializable]
	private class OriginalMaterials {
		public string renderer;
		public string[] materials;
	}

	[Serializable]
	private class Originals {
		public List<OriginalMaterials> renderers = new List<OriginalMaterials>();
	}

	private static string OriginalsPath(string folder) {
		return Path.GetFullPath(folder + "/original_materials.json");
	}

	private static Originals LoadOriginals(string folder) {
		string path = OriginalsPath(folder);
		return File.Exists(path) ? JsonUtility.FromJson<Originals>(File.ReadAllText(path)) : new Originals();
	}

	private static string ObjectId(UnityEngine.Object o) {
		return o != null ? GlobalObjectId.GetGlobalObjectIdSlow(o).ToString() : "";
	}

	private static UnityEngine.Object FindObject(string id) {
		GlobalObjectId gid;
		return GlobalObjectId.TryParse(id, out gid) ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid) : null;
	}

	// <ProjectsFolder>/<Name>/<Name>.mat
	private static string ProjectOfMaterial(Material m) {
		string path = m != null ? AssetDatabase.GetAssetPath(m) : "";
		if (!path.StartsWith(ProjectsFolder + "/")) {
			return null;
		}
		string[] parts = path.Substring(ProjectsFolder.Length + 1).Split('/');
		return parts.Length == 2 && parts[1] == parts[0] + ".mat" ? ProjectsFolder + "/" + parts[0] : null;
	}

	// Restores all objects painted together with the selected ones
	[MenuItem("ArmorPaint/Unpaint Selected", false, 1)]
	private static void ArmorPaintUnpaintOption() {
		List<string> folders = new List<string>();
		foreach (Transform root in Selection.GetTransforms(SelectionMode.TopLevel)) {
			foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true)) {
				foreach (Material m in r.sharedMaterials) {
					string folder = ProjectOfMaterial(m);
					if (folder != null && !folders.Contains(folder)) {
						folders.Add(folder);
					}
				}
			}
		}
		if (folders.Count == 0) {
			EditorUtility.DisplayDialog("Not Painted", "The selected objects do not use an ArmorPaint material.", "OK");
			return;
		}

		List<Pending> pending = LoadPending();
		List<Renderer> renderers = new List<Renderer>();
		List<Material[]> materials = new List<Material[]>();
		int missing = 0;
		foreach (string folder in folders) {
			string name = Path.GetFileName(folder);
			if (pending.Exists(p => p.arm == folder + "/" + name + ".arm")) {
				EditorUtility.DisplayDialog("Already Painting", "'" + name + "' is open in ArmorPaint, close it first.", "OK");
				return;
			}
			Originals originals = LoadOriginals(folder);
			if (originals.renderers.Count == 0) {
				EditorUtility.DisplayDialog("No Original Materials", "No original materials are recorded for '" + name + "'.", "OK");
				return;
			}
			foreach (OriginalMaterials o in originals.renderers) {
				Renderer r = FindObject(o.renderer) as Renderer;
				if (r == null) {
					missing++;
					continue;
				}
				renderers.Add(r);
				materials.Add(Array.ConvertAll(o.materials, id => FindObject(id) as Material));
			}
		}

		string message = "Restore the original materials of:\n" + string.Join("\n", renderers.ConvertAll(r => r.name)) +
			"\n\nThe ArmorPaint project " + string.Join(", ", folders.ConvertAll(Path.GetFileName)) + " will be deleted.";
		if (missing > 0) {
			message += "\n\n" + missing + " objects in scenes that are not loaded will lose their material.";
		}
		if (!EditorUtility.DisplayDialog("Unpaint", message, "Yes", "No")) {
			return;
		}

		Undo.SetCurrentGroupName("Unpaint");
		for (int i = 0; i < renderers.Count; i++) {
			Undo.RecordObject(renderers[i], "Unpaint");
			renderers[i].sharedMaterials = materials[i];
			PrefabUtility.RecordPrefabInstancePropertyModifications(renderers[i]);
		}
		foreach (string folder in folders) {
			AssetDatabase.DeleteAsset(folder);
		}
		Debug.Log("ArmorPaint: restored original materials of " + renderers.Count + " objects");
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
