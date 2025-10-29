using System.Collections.Generic;
using UnityEngine;

public static class PinchManager
{
    private static Dictionary<string, GameObject> grabbedObjects = new Dictionary<string, GameObject>
    {
        {"LeftHand", null},
        {"RightHand", null}
    };

    public static bool IsHandFree(string handName)
    {
        return !grabbedObjects.ContainsKey(handName) || grabbedObjects[handName] == null;
    }

    public static bool IsAnyHandGrabbing()
    {
        return grabbedObjects["LeftHand"] != null || grabbedObjects["RightHand"] != null;
    }

    public static void GrabObject(string handName, GameObject obj)
    {
        grabbedObjects[handName] = obj;
    }

    public static void ReleaseObject(string handName, GameObject obj)
    {
        if (grabbedObjects[handName] == obj)
        {
            grabbedObjects[handName] = null;
        }
    }
}