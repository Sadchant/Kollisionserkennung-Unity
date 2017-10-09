using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class ObjectManager : MonoBehaviour {

    CollisionDetectionManager collisionDetectionManager;

    private List<Transform> allSubTransforms = new List<Transform>();
    private List<int> changedTransforms = new List<int>();

    void AddParentObject(Mesh[] subObjectMeshes, Transform[] subObjectTransforms)
    {
        if (subObjectMeshes.Length > 0) {
            CollisionObject collisionObject = new CollisionObject();
            collisionDetectionManager.AddObjects(collisionObjectList.ToArray());
        }
    }

    void RemoveParentObject()
    {

    }
}
