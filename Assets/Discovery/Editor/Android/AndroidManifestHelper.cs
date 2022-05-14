// 2022-02-20: Modified version of AndroidManifestHelper by Clancey (see Mirror PR #2887)
// Modifications made by Coburn (SoftwareGuy).

// This helper script should only used on Android build targets.
#if UNITY_EDITOR && UNITY_ANDROID
using System.Xml;
using System.IO;

using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

[InitializeOnLoad]
public class AndroidManifestHelper : IPreprocessBuildWithReport, IPostprocessBuildWithReport, IPostGenerateGradleAndroidProject
{
    public int callbackOrder { get { return 99999; } }

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        string manifestFolder = Path.Combine(path, "src/main");
        string sourceFile = manifestFolder + "/AndroidManifest.xml";

        // Load android manfiest file
        var doc = new XmlDocument();
        doc.Load(sourceFile);

        string androidNamepsaceURI;
        var element = (XmlElement)doc.SelectSingleNode("/manifest");
        if (element == null)
        {
            Debug.LogError("Could not find the manifest tag in android manifest.");
            return;
        }

        // Get android namespace URI from the manifest
        androidNamepsaceURI = element.GetAttribute("xmlns:android");
        if (string.IsNullOrEmpty(androidNamepsaceURI))
        {
            Debug.LogError("Could not find the Android Namespace in manifest.");
            return;
        }

        // As the name suggests, add or remove the required things.
        AddOrRemoveTag(doc,
               androidNamepsaceURI,
               "/manifest",
               "uses-permission",
               "android.permission.CHANGE_WIFI_MULTICAST_STATE",
               true,
               false);
        AddOrRemoveTag(doc,
               androidNamepsaceURI,
               "/manifest",
               "uses-permission",
               "android.permission.INTERNET",
               true,
               false);

        // Finally, save it.
        doc.Save(sourceFile);
    }

    private static void AddOrRemoveTag(XmlDocument doc, string @namespace, string path, string elementName, string name, bool required, bool modifyIfFound, params string[] attrs) // name, value pairs	
    {
        XmlNodeList nodes = doc.SelectNodes(path + "/" + elementName);
        XmlElement element = null;
        foreach (XmlElement e in nodes)
        {
            if (name == null || name == e.GetAttribute("name", @namespace))
            {
                element = e;
                break;
            }
        }

        if (required)
        {
            if (element == null)
            {
                XmlNode parent = doc.SelectSingleNode(path);
                element = doc.CreateElement(elementName);
                element.SetAttribute("name", @namespace, name);
                parent.AppendChild(element);
            }

            for (int i = 0; i < attrs.Length; i += 2)
            {
                if (modifyIfFound || string.IsNullOrEmpty(element.GetAttribute(attrs[i], @namespace)))
                {
                    if (attrs[i + 1] != null)
                    {
                        element.SetAttribute(attrs[i], @namespace, attrs[i + 1]);
                    }
                    else
                    {
                        element.RemoveAttribute(attrs[i], @namespace);
                    }
                }
            }
        }
        else
        {
            if (element != null && modifyIfFound)
            {
                element.ParentNode.RemoveChild(element);
            }
        }
    }

    public void OnPostprocessBuild(BuildReport report)
    {
        // Unused.
    }

    public void OnPreprocessBuild(BuildReport report)
    {
        // Unused.
    }

}
#endif
