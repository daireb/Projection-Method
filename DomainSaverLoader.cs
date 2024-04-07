using System.IO;
using UnityEngine;

public static class DomainSaverLoader
{
    private static string GetFilePath(string fileName)
    {
        return Path.Combine(Application.persistentDataPath, fileName);
    }

    public static void SaveData(int[] array, string fileName)
    {
        IntArrayData data = new IntArrayData { myArray = array };
        string json = JsonUtility.ToJson(data);
        string filePath = GetFilePath(fileName);
        File.WriteAllText(filePath, json);
        Debug.Log("Saved data to " + filePath);
    }

    public static int[] LoadData(string fileName)
    {
        string filePath = GetFilePath(fileName);
        if (File.Exists(filePath))
        {
            string json = File.ReadAllText(filePath);
            IntArrayData data = JsonUtility.FromJson<IntArrayData>(json);
            Debug.Log("Loaded data from " + filePath);
            return data.myArray;
        }
        else
        {
            Debug.LogError("File not found: " + filePath);
            return null; // or return an empty array depending on your needs
        }
    }
}
