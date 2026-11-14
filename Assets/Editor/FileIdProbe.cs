// CI only (.github/workflows/fileids.yml): lists every object Unity makes from the .fbx files in Assets/FileIdProbe
// with its local fileID, so the IDs a model's prefab gets can be worked out for the on-phone VRChat import.
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class FileIdProbe
{
    static string PathOf(Transform t)
    {
        var s = t.name;
        for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
        return s;
    }

    public static void Run()
    {
        var o = new StringBuilder();
        if (Directory.Exists("Assets/FileIdProbe"))
            foreach (var path in Directory.GetFiles("Assets/FileIdProbe", "*.fbx"))
            {
                var imp = AssetImporter.GetAtPath(path) as ModelImporter;
                o.AppendLine("FILE|" + Path.GetFileName(path) + "|fileIdsGeneration " + (imp != null ? new SerializedObject(imp).FindProperty("m_FileIdsGeneration")?.intValue.ToString() : "?"));
                foreach (var a in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (a == null) continue;
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(a, out string guid, out long id);
                    string p = a is Component c ? PathOf(c.transform) : a is GameObject g ? PathOf(g.transform) : "";
                    o.AppendLine("OBJ|" + Path.GetFileName(path) + "|" + a.GetType().Name + "|" + a.name + "|" + p + "|" + id);
                }
            }
        File.WriteAllText("fileids.txt", o.ToString());
        Debug.Log("FileIdProbe: " + o.Length + " chars");
    }
}
