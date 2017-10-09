using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class SceneManager : MonoBehaviour {

    

    private void PassSceneObjects()
    {
        List<CollisionObject> collisionObjectList = new List<CollisionObject>();

        GameObject[] allObjects = FindObjectsOfType<GameObject>();
        
    }

	// Use this for initialization
	void Start () {
        collisionDetectionManager = new CollisionDetectionManager();

        PassSceneObjects();
    }
	

    void LateUpdate()
    {
        collisionDetectionManager.Frame();
    }

    private void OnDestroy()
    {
        collisionDetectionManager.Shutdown();
    }
}
