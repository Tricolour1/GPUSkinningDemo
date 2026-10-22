using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GpuSkinningDemo.Editor
{
    public sealed class BakingWindow : EditorWindow
    {
        [SerializeField] private GameObject source;
        [SerializeField] private List<AnimationClip> clips = new List<AnimationClip>();
        [SerializeField] private int sampleRate = 30;
        [SerializeField] private bool highPrecision;
        [SerializeField] private DefaultAsset outputFolder;
        private string status;
        private Vector2 scroll;

        [MenuItem("Tools/GPU Skinning/Baking Window")]
        public static void Open() => GetWindow<BakingWindow>("GPU Skinning Baker");

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Generic animation to GPU bone texture", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("One renderer, one material, up to four bone weights. Source assets remain unchanged. Every bake creates a new output folder.", MessageType.Info);
            source = (GameObject)EditorGUILayout.ObjectField("Source Prefab", source, typeof(GameObject), false);
            sampleRate = EditorGUILayout.IntSlider("Sample rate", sampleRate, 1, 120);
            highPrecision = EditorGUILayout.Toggle("RGBAFloat (otherwise Half)", highPrecision);
            outputFolder = (DefaultAsset)EditorGUILayout.ObjectField("Output folder", outputFolder, typeof(DefaultAsset), false);
            EditorGUILayout.Space();
            for (int i = 0; i < clips.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                clips[i] = (AnimationClip)EditorGUILayout.ObjectField("Clip " + i, clips[i], typeof(AnimationClip), false);
                if (GUILayout.Button("Remove", GUILayout.Width(65))) { clips.RemoveAt(i); i--; }
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("Add clip")) clips.Add(null);
            if (GUILayout.Button("Bake and validate", GUILayout.Height(36)))
            {
                try
                {
                    string folder = outputFolder == null ? DemoBuilder.Generated : AssetDatabase.GetAssetPath(outputFolder);
                    var data = AnimationBaker.Bake(source, clips.ToArray(), sampleRate,
                        highPrecision ? TextureFormat.RGBAFloat : TextureFormat.RGBAHalf, folder);
                    status = data.validation;
                    Selection.activeObject = data;
                    EditorGUIUtility.PingObject(data);
                }
                catch (Exception e) { status = e.Message; Debug.LogException(e); }
            }
            if (GUILayout.Button("Create self-contained sample and demo scene")) DemoBuilder.CreateDemo();
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);
            EditorGUILayout.EndScrollView();
        }
    }
}
