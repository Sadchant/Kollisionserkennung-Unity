using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class SceneManager : MonoBehaviour {

    CollisionDetectionManager collisionDetectionManager;

    private void PassSceneObjects()
    {
        List<CollisionObject> collisionObjectList = new List<CollisionObject>();

        GameObject[] allObjects = FindObjectsOfType<GameObject>();
        foreach (GameObject curGameObject in allObjects)
        {
            if (curGameObject.GetComponent<MeshFilter>() != null)
            {
                //curGameObject.transform.localToWorldMatrix * previousLocalToWorldMatrix.inverse
                CollisionObject curCollisionObject = new CollisionObject();
                curCollisionObject.meshArray = new Mesh[1] { curGameObject.GetComponent<MeshFilter>().mesh };
                collisionObjectList.Add(curCollisionObject);
            }
        }
        if (collisionObjectList.Count > 0)
            collisionDetectionManager.AddObjects(collisionObjectList.ToArray());
    }

	// Use this for initialization
	void Start () {
        collisionDetectionManager = new CollisionDetectionManager();

        PassSceneObjects();
    }
	
	// Update is called once per frame
	void Update () {
        collisionDetectionManager.Frame();
	}

    private void OnDestroy()
    {
        collisionDetectionManager.Shutdown();
    }
}
