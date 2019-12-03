﻿using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEditor;

public class ArmorPaint {

	private static string MeshToObj(MeshFilter mf, string name) {
		Mesh m = mf.sharedMesh;
		Material[] mats = mf.GetComponent<Renderer>().sharedMaterials;

		StringBuilder sb = new StringBuilder();
		sb.Append(string.Format("o {0}\n", name));

		foreach(Vector3 v in m.vertices) {
			sb.Append(string.Format("v {0} {1} {2}\n", v.x, v.y, v.z));
		}

		foreach(Vector3 v in m.normals) {
			sb.Append(string.Format("vn {0} {1} {2}\n",v.x, v.y, v.z));
		}

		foreach(Vector3 v in m.uv) {
			sb.Append(string.Format("vt {0} {1}\n", v.x, v.y));
		}

		for (int material = 0; material < m.subMeshCount; material++) {
			int[] triangles = m.GetTriangles(material);
			for (int i = 0; i < triangles.Length; i += 3) {
				sb.Append(string.Format("f {0}/{0}/{0} {1}/{1}/{1} {2}/{2}/{2}\n",
					triangles[i]+1, triangles[i+1]+1, triangles[i+2]+1));
			}
		}

		return sb.ToString();
	}

	[MenuItem("ArmorPaint/Paint Selected")]
	private static void ArmorPaintPaintOption() {
		if (Selection.gameObjects.Length == 0) return;

		string name = Selection.gameObjects[0].name;
		Transform t = Selection.gameObjects[0].transform;
		MeshFilter mf = t.GetComponent<MeshFilter>();
		if (mf == null) return;

		string path;
		if (!EditorPrefs.HasKey("ArmorPaintPath")) {
			path = ArmorPaintPathOption();
		}
		else {
			path = EditorPrefs.GetString("ArmorPaintPath");
		}
		string filepath = path + "/data/temp.obj";
		string binpath = path + "/ArmorPaint" + GetBinaryExtension();

		using (StreamWriter sw = new StreamWriter(filepath)) {
			sw.Write(MeshToObj(mf, name));
		}

		System.Diagnostics.Process.Start(binpath, filepath);
	}

	[MenuItem("ArmorPaint/Set Path")]
	private static string ArmorPaintPathOption() {
		string path = EditorUtility.OpenFolderPanel("Select folder where ArmorPaint is located", "", "");
		EditorPrefs.SetString("ArmorPaintPath", path);
		return path;
	}

	private static string GetBinaryExtension() {
		if (SystemInfo.operatingSystemFamily == OperatingSystemFamily.Windows) {
			return ".exe";
		}
		else if (SystemInfo.operatingSystemFamily == OperatingSystemFamily.MacOSX) {
			return ".app";
		}
		else {
			return "";
		}
	}
}
