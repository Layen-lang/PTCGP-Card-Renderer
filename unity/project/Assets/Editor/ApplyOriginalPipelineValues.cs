using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class ApplyOriginalPipelineValues
{
    [Serializable] class Entry { public string asset,path; public double value; }
    [Serializable] class Plan { public Entry[] entries; }
    public static void Apply(UnityEngine.Object pipeline, UnityEngine.Object renderer)
    {
        var plan = JsonUtility.FromJson<Plan>(File.ReadAllText("Assets/Editor/OriginalPipelineValues.json"));
        var p = new SerializedObject(pipeline); var r = new SerializedObject(renderer);
        foreach (var entry in plan.entries) {
            var prop = (entry.asset == "pipeline" ? p : r).FindProperty(entry.path);
            if (prop == null) throw new InvalidOperationException("Missing original field: "+entry.asset+"."+entry.path);
            switch (prop.propertyType) {
                case SerializedPropertyType.Boolean: prop.boolValue = entry.value != 0; break;
                case SerializedPropertyType.Float: prop.doubleValue = entry.value; break;
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.Enum:
                case SerializedPropertyType.LayerMask: prop.longValue = (long)entry.value; break;
                default: throw new InvalidOperationException("Unsupported original field: "+entry.path+" "+prop.propertyType);
            }
        }
        p.ApplyModifiedPropertiesWithoutUndo(); r.ApplyModifiedPropertiesWithoutUndo();
    }
}
