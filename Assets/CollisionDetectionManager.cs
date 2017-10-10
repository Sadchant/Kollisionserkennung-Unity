using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System;

public struct CollisionObject
{
    public Mesh[] meshArray;
    public Matrix4x4[] matrices;
    public Transform[] transforms;
};

struct ReduceData
{
    public int firstStepStride;
    public int inputSize;
    public int bool_OutputIsInput;
};

struct FillCounterTreesData
{
    public int[] objectCount;
    public int[][] treeSizeInLevel;
};

struct CellTrianglePair
{
    uint cellID;
    uint triangleID;
    uint objectID;
};


public class CollisionDetectionManager {

    private List<CollisionObject> sceneCollisionObjects;

    private int m_VertexCount;
    private int m_TriangleCount;
    private int m_ObjectCount;
    private int m_GroupResult_Count; // wie groß ist das Ergebnis nach einem Reduce von Buffern der Größe m_VertexCount
    private int m_TreeSize;
    private int m_CounterTreesSize;
    int m_CellTrianglePairsCount;
    int m_SortIndicesCount;
    int m_TrianglePairsCount;
    int m_IntersectionCentersCount;
    bool m_CopyTo1;

    private int m_SubObjectCount;


    List<ComputeShader> m_ComputeShaderList;
    private ComputeShader m_CurComputeShader;

    private ComputeShader m_UpdateVertiecesComputeShader;

    private Vector3[] m_Vertices;
    private int[] m_Triangles;
    private uint[] m_ObjectsLastIndices;
    private uint[] m_SubObjectsLastVertexIndices;

    private CellTrianglePair[] m_CellTrianglePairs_Zero;

    private ComputeBuffer m_Vertex_Buffer; // alle Punkte der Szene, deren Objekte kollidieren
    private ComputeBuffer m_Triangle_Buffer; // alle Dreiecke der Szene, deren Objekte kollidieren
    private ComputeBuffer m_ObjectsLastIndices_Buffer; // die Indices im Dreieck-Buffer, die das letzte Dreieck eines Objektes markieren
    private ComputeBuffer m_BoundingBox_Buffer; // die Bounding Boxes für jedes Dreieck
    private ComputeBuffer m_GroupMinPoint_Buffer; // Ergebnisbuffer einer Reduktion: beinhaltet nach einem Durchlauf die MinimalPunkte, die jede Gruppe berechnet hat
    private ComputeBuffer m_GroupMaxPoint_Buffer; // das selbe für die MaximalPunkte
    private ComputeBuffer m_CounterTrees_Buffer; // die Countertrees für alle Objekte
    private ComputeBuffer m_GlobalCounterTree_Buffer; // die Countertrees für alle Objekte
    private ComputeBuffer m_TypeTree_Buffer; // der Typetree für den globalen Tree
    private ComputeBuffer m_LeafIndexTree_Buffer; // in diesem Tree steht an jeder Stelle die ID der Zelle, die in diesem Zweig Blatt ist
    private ComputeBuffer m_CellTrianglePairs_Buffer; // enthält alle für die Kollisionsberechnung relevanten Zellen-Dreicks-Paare
    private ComputeBuffer m_SortIndices_Buffer; // Indices für den RadixSort, wo wird pro Bit hinsortiert?
    private ComputeBuffer m_CellTrianglePairsBackBuffer_Buffer; // dient als BackBuffer beim Sortieren
    private ComputeBuffer m_TrianglePairs_Buffer; // enthält alle BoundingBox-Ids, die sich überschneiden
    private ComputeBuffer m_IntersectingObjects_Buffer; // enthält für jedes Objekt einen Eintrag, ob es mit anderen kollidiert
    private ComputeBuffer m_IntersectCenters_Buffer; // enthält alle Mittelpunkte von Dreiecks-Kollisionen

    private ComputeBuffer m_SubObjectsLastVertexIndices_Buffer; // enthält alle Mittelpunkte von Dreiecks-Kollisionen

    uint[] m_Results10_1_IntersectingObjects; // wird von der GPU befüllt!
    Vector3[] m_Results10_2_IntersectionPoints; // wird von der GPU befüllt!

    FillCounterTreesData m_FillCounterTreesData;


    void InitComputeShaderList()
    {
        m_UpdateVertiecesComputeShader = Resources.Load<ComputeShader>("ComputeShader/0_UpdateObjectVertices");
        m_ComputeShaderList = new List<ComputeShader>();
        m_ComputeShaderList.Add(Resources.Load<ComputeShader>("ComputeShader/1_BoundingBoxes"));
        m_ComputeShaderList.Add(Resources.Load<ComputeShader>("ComputeShader/2_SceneBoundingBox"));
        m_ComputeShaderList.Add(Resources.Load<ComputeShader>("ComputeShader/3_FillCounterTrees"));
        m_ComputeShaderList.Add(Resources.Load<ComputeShader>("ComputeShader/4_GlobalCounterTree"));
        m_ComputeShaderList.Add(Resources.Load<ComputeShader>("ComputeShader/5_FillTypeTree"));
        m_ComputeShaderList.Add(Resources.Load<ComputeShader>("ComputeShader/6_FillLeafIndexTree"));
        m_ComputeShaderList.Add(Resources.Load<ComputeShader>("ComputeShader/7_CellTrianglePairs"));
        m_ComputeShaderList.Add(Resources.Load<ComputeShader>("ComputeShader/8_1_RadixSort_ExclusivePrefixSum"));
        m_ComputeShaderList.Add(Resources.Load<ComputeShader>("ComputeShader/8_2_RadixSort_ExclusivePrefixSum"));
        m_ComputeShaderList.Add(Resources.Load<ComputeShader>("ComputeShader/8_3_RadixSort_Sort"));
        m_ComputeShaderList.Add(Resources.Load<ComputeShader>("ComputeShader/9_FindTrianglePairs"));
        m_ComputeShaderList.Add(Resources.Load<ComputeShader>("ComputeShader/10_TriangleIntersections"));
        m_ComputeShaderList.Add(Resources.Load<ComputeShader>("ComputeShader/11_ZeroIntersectionCenters"));
    }

    // Use this for initialization
    public CollisionDetectionManager () {
        sceneCollisionObjects = new List<CollisionObject>();
        InitComputeShaderList();
    }

    public void Shutdown()
    {
        ReleaseBuffers();
    }

    private void RecreateSceneData()
    {
        CreateVertexAndTriangleArray();
        CreateSceneBuffers();
        Int4ArrayTo1DArray(m_FillCounterTreesData.treeSizeInLevel);
    }


    public void AddObject(CollisionObject newCollisionObject)
    {
        sceneCollisionObjects.Add(newCollisionObject);
        RecreateSceneData();
    }

    public void AddObjects(CollisionObject[] newCollisionObjects)
    {
        sceneCollisionObjects.AddRange(newCollisionObjects);
        RecreateSceneData();
    }

    public void RemoveObject(CollisionObject deleteThisCollisionObject)
    {
        sceneCollisionObjects.Remove(deleteThisCollisionObject);
        RecreateSceneData();
    }

    double Log2(int value)
    {
        return Mathf.Log10(value) / (Mathf.Log10(2));
    }
    // erzeugt die Arrays, die zum Initialisieren der Buffer benötigt werden
    private void CreateVertexAndTriangleArray()
    {
        //Zuerst ausrechnen, wie groß das Triangle- und das Vertex-Array sein sollte
        m_VertexCount = 0;
        int subObjectCounter = 0;
        foreach (CollisionObject collisionObject in sceneCollisionObjects)
        {
            foreach(Mesh mesh in collisionObject.meshArray)
            {
                m_VertexCount += mesh.vertexCount;
                m_TriangleCount += mesh.triangles.Length / 3;
                subObjectCounter++;
            }            
        }
        m_SubObjectCount = subObjectCounter;

        m_ObjectCount = sceneCollisionObjects.Count;

        m_Vertices = new Vector3[m_VertexCount];
        m_Triangles = new int[m_TriangleCount * 3];
        m_ObjectsLastIndices = new uint[m_ObjectCount]; // es gibt so viele Einträge wie Objekte
        m_SubObjectsLastVertexIndices = new uint[m_SubObjectCount]; // es gibt so viele Einträge wie Objekte

        int curAllVerticesCount = 0; // zähle alle VertexCounts für jedes Objekt zusammen
        int curAllIndicesCount = 0; // zähle alle IndexCounts für jedes Objekt zusammen
        // gehe über alle Objekte
        for (int i = 0; i < m_ObjectCount; i++)
        {
            Mesh[] curMeshArray = sceneCollisionObjects[i].meshArray;
            foreach (Mesh curMesh in curMeshArray)
            {
                int curMeshVertexCount = curMesh.vertexCount;
                int curMeshIndexCount = curMesh.triangles.Length;

                // gehe über alle Vertices und kopiere die Vertices dieses Objekts in den Szenen-Vertexbuffer
                for (int j = 0; j < curMeshVertexCount; j++) // iteriere über jeden Vertex in modelData
                {
                    m_Vertices[curAllVerticesCount + j] = curMesh.vertices[j];
                }

                // gehe über alle Indices und kopiere modifizierte Indices in den Szenen-Indexbuffer
                for (int k = 0; k < curMeshIndexCount; k++)
                {
                    // addiere die Menge der Vertices aller bisherigen Objekte (Objekte, jedes Objekt hat ein MeshArray!), da die Indices auf den zusammenkopierten Vertexbuffer verweisen
                    m_Triangles[curAllIndicesCount + k] = curMesh.triangles[k] + curAllVerticesCount;
                }

                curAllVerticesCount += curMeshVertexCount;
                curAllIndicesCount += curMeshIndexCount;
                m_SubObjectsLastVertexIndices[i] = (uint)curAllVerticesCount - 1;
            }

            // schreibe außerdem den letzten Index des Objektes in m_ObjectLastIndices
            m_ObjectsLastIndices[i] = (uint)curAllIndicesCount / 3 - 1;
        }
        m_CellTrianglePairsCount = (int)Mathf.Ceil(m_TriangleCount * 3.5f);
        m_SortIndicesCount = (int)Mathf.Pow(2, (int)Mathf.Ceil((float)Log2(m_CellTrianglePairsCount))) + 1; // + 1 für eine komplette exklusive Prefix Summe
        m_TrianglePairsCount = m_TriangleCount * 30;
        m_IntersectionCentersCount = m_TriangleCount * 8;
    }

    // gib alle Buffer, Shader Resource Views und Unordered Access Views frei
    void ReleaseBuffers()
    {
        if (m_Vertex_Buffer != null)
            m_Vertex_Buffer.Release();
        if (m_Triangle_Buffer != null)
            m_Triangle_Buffer.Release();
        if (m_ObjectsLastIndices_Buffer != null)
            m_ObjectsLastIndices_Buffer.Release();
        if (m_BoundingBox_Buffer != null)
            m_BoundingBox_Buffer.Release();
        if (m_GroupMinPoint_Buffer != null)
            m_GroupMinPoint_Buffer.Release();
        if (m_GroupMaxPoint_Buffer != null)
            m_GroupMaxPoint_Buffer.Release();
        if (m_CounterTrees_Buffer != null)
            m_CounterTrees_Buffer.Release();
        if (m_GlobalCounterTree_Buffer != null)
            m_GlobalCounterTree_Buffer.Release();
        if (m_TypeTree_Buffer != null)
            m_TypeTree_Buffer.Release();
        if (m_LeafIndexTree_Buffer != null)
            m_LeafIndexTree_Buffer.Release();
        if (m_CellTrianglePairs_Buffer != null)
            m_CellTrianglePairs_Buffer.Release();
        if (m_SortIndices_Buffer != null)
            m_SortIndices_Buffer.Release();
        if (m_CellTrianglePairsBackBuffer_Buffer != null)
            m_CellTrianglePairsBackBuffer_Buffer.Release();
        if (m_TrianglePairs_Buffer != null)
            m_TrianglePairs_Buffer.Release();
        if (m_IntersectingObjects_Buffer != null)
            m_IntersectingObjects_Buffer.Release();
        if (m_IntersectCenters_Buffer != null)
            m_IntersectCenters_Buffer.Release();
        if (m_SubObjectsLastVertexIndices_Buffer != null)
            m_SubObjectsLastVertexIndices_Buffer.Release();        
}

    void CreateSceneBuffers()
    {
        // Buffer, ShaderResourceViews und UnorderedAccessViews müssen released werden (falls etwas in ihnen ist), bevor sie neu created werden!
        ReleaseBuffers();
        m_Vertex_Buffer = new ComputeBuffer(m_VertexCount, sizeof(float)*3, ComputeBufferType.Default );
        m_Vertex_Buffer.SetData(m_Vertices);
        m_Triangle_Buffer = new ComputeBuffer(m_TriangleCount, sizeof(uint) * 3, ComputeBufferType.Default);
        m_Triangle_Buffer.SetData(m_Triangles);
        m_ObjectsLastIndices_Buffer = new ComputeBuffer(m_ObjectCount, sizeof(uint), ComputeBufferType.Default);
        m_ObjectsLastIndices_Buffer.SetData(m_ObjectsLastIndices);
        m_BoundingBox_Buffer = new ComputeBuffer(m_TriangleCount, sizeof(float) * 6, ComputeBufferType.Default);
        // BoundingBox_Buffer und Result_Buffer werd ja im Shader befüllt, müssen also nicht mit Daten initialisiert werdenS

        m_GroupResult_Count = (int)Mathf.Ceil((float)m_VertexCount / (2 * Constants.B_SCENEBOUNDINGBOX_XTHREADS));
        m_GroupMinPoint_Buffer = new ComputeBuffer(m_GroupResult_Count, sizeof(float) * 3, ComputeBufferType.Default);
        m_GroupMaxPoint_Buffer = new ComputeBuffer(m_GroupResult_Count, sizeof(float) * 3, ComputeBufferType.Default);

        m_TreeSize = 0;
        // Berechne aus LEVELS die TreeSize (es wird die Anzahl der Zellen pro Level zusammengerechnet)
        m_FillCounterTreesData.treeSizeInLevel = new int[Constants.SUBDIVS + 1][];
        for (int i = 0; i <= Constants.SUBDIVS; i++)
        {
            m_TreeSize += (int)Mathf.Pow(8, i); // es gibt pro Level 8 hoch aktuelles Level Unterteilungen
            m_FillCounterTreesData.treeSizeInLevel[i] = new int[4] { m_TreeSize,0,0,0 };
        }
        m_FillCounterTreesData.objectCount = new int[4] { m_ObjectCount, 0, 0, 0 };

        m_CounterTreesSize = m_ObjectCount * m_TreeSize;
        uint[] counterTrees_0s = Enumerable.Repeat(0u, m_CounterTreesSize).ToArray(); // Dient nur dazu, den counterTrees-Buffer mit 0en zu füllen

        m_CounterTrees_Buffer = new ComputeBuffer(m_CounterTreesSize, sizeof(uint), ComputeBufferType.Default);
        m_CounterTrees_Buffer.SetData(counterTrees_0s);

        m_GlobalCounterTree_Buffer = new ComputeBuffer(m_TreeSize, sizeof(uint), ComputeBufferType.Default);

        m_TypeTree_Buffer = new ComputeBuffer(m_TreeSize, sizeof(uint), ComputeBufferType.Default);
        m_LeafIndexTree_Buffer = new ComputeBuffer(m_TreeSize, sizeof(uint), ComputeBufferType.Default);
        m_CellTrianglePairs_Buffer = new ComputeBuffer(m_CellTrianglePairsCount, sizeof(uint) * 3, ComputeBufferType.Counter);
        m_SortIndices_Buffer = new ComputeBuffer(m_SortIndicesCount, sizeof(uint)*4, ComputeBufferType.Default);
        m_CellTrianglePairsBackBuffer_Buffer = new ComputeBuffer(m_CellTrianglePairsCount, sizeof(uint)*4, ComputeBufferType.Default);
        m_TrianglePairs_Buffer = new ComputeBuffer(m_TrianglePairsCount, sizeof(uint)*4, ComputeBufferType.Counter);
        m_IntersectingObjects_Buffer = new ComputeBuffer(m_ObjectCount, sizeof(uint), ComputeBufferType.Default);
        m_IntersectCenters_Buffer = new ComputeBuffer(m_IntersectionCentersCount, sizeof(float)*3, ComputeBufferType.Counter);

        m_SubObjectsLastVertexIndices_Buffer = new ComputeBuffer(m_SubObjectCount, sizeof(uint), ComputeBufferType.Default);
        m_SubObjectsLastVertexIndices_Buffer.SetData(m_SubObjectsLastVertexIndices);

        m_CellTrianglePairs_Zero = new CellTrianglePair[m_CellTrianglePairsCount];
        Array.Clear(m_CellTrianglePairs_Zero, 0, m_CellTrianglePairsCount);
    }

    // konvertiert ein 2D-Array, was aus x 4er-Ints besteht in ein eindimensionales Array, damit Unity es an Shader übergeben kann
    private int[] Int4ArrayTo1DArray(int[][] _2DArray)
    {
        int[] resultArray = new int[_2DArray.Length * 4];
        for(int i = 0; i < _2DArray.Length; i++)
        {
            for (int j = 0; j<4; j++)
            {
                resultArray[i * 4 + j] = _2DArray[i][j];
            }
        }
        return resultArray;
    }

    // ******* 0. Wende eine Manipulation eines Objekts auf die dazugehörigen Vertices an *******
    void _0_UpdateObjectVertices(int objectID, int subObjectID, Matrix4x4 updateMatrix)
    {
        Mesh updateThisMesh = sceneCollisionObjects[objectID].meshArray[subObjectID];
        int meshSize = updateThisMesh.vertexCount;
        m_CurComputeShader = m_UpdateVertiecesComputeShader;
        int kernelID = m_CurComputeShader.FindKernel("main");

        int xThreadGroups = (int)Mathf.Ceil(meshSize / 1024.0f);

        m_CurComputeShader = m_UpdateVertiecesComputeShader;

        m_CurComputeShader.SetBuffer(kernelID, "vertexBuffer", m_Vertex_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "objectsLastVertexIndices", m_SubObjectsLastVertexIndices_Buffer);

        float[] matrixArray = new float[]
        {
        updateMatrix[0,0], updateMatrix[1, 0], updateMatrix[2, 0],
        updateMatrix[0,1], updateMatrix[1, 1], updateMatrix[2, 1],
        updateMatrix[0,2], updateMatrix[1, 2], updateMatrix[2, 2],
        updateMatrix[0,3], updateMatrix[1, 3], updateMatrix[2, 3],
        };
        m_CurComputeShader.SetFloats("updateMatrix", matrixArray);

        int subObjectGlobalID = 0;
        for (int i= 0; i < objectID; i++)
        {
            subObjectGlobalID += sceneCollisionObjects[i].meshArray.Length;
        }
        subObjectGlobalID += subObjectID;
        m_CurComputeShader.SetInt("subObjectID", subObjectGlobalID);


        m_CurComputeShader.Dispatch(kernelID, xThreadGroups, 1, 1);
    }

    // ******* 1. Berechne Bounding Boxes für jedes Dreieck *******
    void _1_BoundingBoxes()
    {
        int kernelID;
        // ####### Berechne Bounding Boxes für jedes Dreieck #######

        m_CurComputeShader = m_ComputeShaderList[0];
        kernelID = m_CurComputeShader.FindKernel("main");

        int xThreadGroups = (int)Mathf.Ceil(m_TriangleCount / 1024.0f);

        m_CurComputeShader = m_ComputeShaderList[0];

        m_CurComputeShader.SetBuffer(kernelID, "vertexBuffer", m_Vertex_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "triangleBuffer", m_Triangle_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "boundingBoxBuffer", m_BoundingBox_Buffer);

        m_CurComputeShader.Dispatch(kernelID, xThreadGroups, 1, 1);
    }

    // ******* 2. Berechne Bounding Box für die gesamte Szene *******
    void _2_SceneCoundingBox()
    {
        // ####### Berechne Bounding Box für die gesamte Szene #######
        m_CurComputeShader = m_ComputeShaderList[1];
        int kernelID = m_CurComputeShader.FindKernel("main");

        m_CurComputeShader.SetBuffer(kernelID, "minInput", m_Vertex_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "maxInput", m_Vertex_Buffer);

        // GroupMinMaxPoint: Output im ersten Durchgang, Input und Output in allen anderen
        m_CurComputeShader.SetBuffer(kernelID, "minOutput", m_GroupMinPoint_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "maxOutput", m_GroupMaxPoint_Buffer);

        int threadCount, groupCount = 0, firstStepStride, inputSize;
        bool firstStep = true;
        bool outputIsInput = false; // teilt den Shader mit, dass er nicht mehr aus Vertices liest und stattdessen den Output als Input benutzen soll

        // Hier die einzigartige und selten gesehene do-while-Schleife:
        do
        {
            // im allerersten Durchlauf werden die Vertices als Input für die Berechnungen der Szenen-BoundingBox benutzt
            // der ThreadCount (wie viele Threads werden praktisch benötigt?) berechnet sich aus vertexCount
            if (firstStep)
            {
                threadCount = (int)Mathf.Ceil(m_VertexCount / 2.0f);
                inputSize = m_VertexCount;
                firstStep = false;
            }
            // ansonsten wird als Input das Ergebnis des letzten Schleifen-Durchlaufs in den groupMin/MaxPoint_Buffern benutzt
            // der ThreadCount berechnet sich aus der Anzahl der Gruppen im letzten Schleifen-Durchlauf
            else
            {
                threadCount = (int)Mathf.Ceil(groupCount / 2.0f);
                inputSize = groupCount; // ab dem zweiten Durchlauf werden ja gruppenErgebnisse weiterverarbeitet, also entspricht die InputSize dem letzten groupCount
                outputIsInput = true; // alle außer dem ersten Durchlauf dürfen den Input manipulieren
            }
            groupCount = (int)Mathf.Ceil((float)threadCount / Constants.B_SCENEBOUNDINGBOX_XTHREADS);
            firstStepStride = groupCount * Constants.B_SCENEBOUNDINGBOX_XTHREADS;
            // Struct für den Constant Buffer
            ReduceData reduceData = new ReduceData();
            reduceData.firstStepStride = firstStepStride;
            reduceData.inputSize = inputSize;
            if (outputIsInput)
                reduceData.bool_OutputIsInput = 1;
            else
                reduceData.bool_OutputIsInput = 0;

            m_CurComputeShader.SetInt("minOutput", reduceData.firstStepStride);
            m_CurComputeShader.SetInt("inputSize", reduceData.inputSize);
            m_CurComputeShader.SetInt("bool_OutputIsInput", reduceData.bool_OutputIsInput);

            m_CurComputeShader.Dispatch(kernelID, groupCount, 1, 1);
        } while (groupCount > 1);
        // solange mehr als eine Gruppe gestartet werden muss, werden die Min-MaxPoints nicht auf ein Ergebnis reduziert sein,
        // da es ja immer ein Ergebnis pro Gruppe berechnet wird
    }

    // ******* 3. Befülle Countertrees mit den Daten für jedes Objekt *******
    void _3_FillCounterTrees()
    {
        m_CurComputeShader = m_ComputeShaderList[2];
        int kernelID = m_CurComputeShader.FindKernel("main");

        m_CurComputeShader.SetBuffer(kernelID, "sceneMinPoints", m_GroupMinPoint_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "sceneMaxPoints", m_GroupMaxPoint_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "boundingBoxBuffer", m_BoundingBox_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "counterTrees", m_CounterTrees_Buffer);

        m_CurComputeShader.SetBuffer(kernelID, "objectsLastIndices", m_ObjectsLastIndices_Buffer);

        m_CurComputeShader.SetInts("objectCount", m_FillCounterTreesData.objectCount);
        m_CurComputeShader.SetInts("treeSizeInLevel", Int4ArrayTo1DArray(m_FillCounterTreesData.treeSizeInLevel));
        int xThreadGroups = (int)Mathf.Ceil(m_TriangleCount / 1024.0f);
        m_CurComputeShader.Dispatch(kernelID, xThreadGroups, 1, 1);

        //int[] resultArray = new int[m_CounterTreesSize];
        //m_CounterTrees_Buffer.GetData(resultArray);
        //int result = resultArray[0];
        //int result1 = resultArray[1];
    }

    // ******* 4.Befülle den GlobalCounterTree mit den Daten, wie viele Überschneidungstets es pro Zelle gibt *******
    void _4_GlobalCounterTree()
    {
        m_CurComputeShader = m_ComputeShaderList[3];
        int kernelID = m_CurComputeShader.FindKernel("main");

        m_CurComputeShader.SetBuffer(kernelID, "counterTrees", m_CounterTrees_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "globalCounterTree", m_GlobalCounterTree_Buffer);

        m_CurComputeShader.SetInts("objectCount", m_FillCounterTreesData.objectCount);
        m_CurComputeShader.SetInts("treeSizeInLevel", Int4ArrayTo1DArray(m_FillCounterTreesData.treeSizeInLevel));

        int xThreadGroups = (int)Mathf.Ceil(m_TreeSize / 1024.0f);

        m_CurComputeShader.Dispatch(kernelID, xThreadGroups, 1, 1);
    }

    // ******* 5. Trage im GlobalCounterTree die Werte der optimierten Struktur ein und speichere die Struktur in TypeTree *******
    void _5_FillTypeTree()
    {
        m_CurComputeShader = m_ComputeShaderList[4];
        int kernelID = m_CurComputeShader.FindKernel("main");

        m_CurComputeShader.SetBuffer(kernelID, "globalCounterTree", m_GlobalCounterTree_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "typeTree", m_TypeTree_Buffer);
        m_CurComputeShader.SetInts("treeSizeInLevel", Int4ArrayTo1DArray(m_FillCounterTreesData.treeSizeInLevel));


        int _3DGroupCount, curStartLevel, curLevelResolution;
        // es gibt immer einen Level mehr als SUBDIVS, also wird mit dem Index SUBDIVS auf den letzten Level zugegriffen,
        // wir suchen den vorletzten Level, also SUBDIVS - 1!
        curStartLevel = Constants.SUBDIVS - 1;
        while (curStartLevel >= 0)
        {
            curLevelResolution = (int)Mathf.Pow(8, curStartLevel); // 8 hoch den aktuellen Level ergibt die Auflösung für den Level
                                                             // die dritte Wurzel von der aktuellen Auflösung geteilt durch 512 ergibt die Anzahl an Gruppen die erzeugt werden müssen pro Dimension
            _3DGroupCount = (int)Mathf.Ceil(Mathf.Pow(curLevelResolution / 512.0f, 1.0f / 3.0f));

            // Constant Buffer updaten
            m_CurComputeShader.SetInt("startLevel", curStartLevel);

            m_CurComputeShader.Dispatch(kernelID, _3DGroupCount, _3DGroupCount, _3DGroupCount);

            curStartLevel -= 4; // der Shader kann mit 512 Threads / Gruppe die Eingabemenge um die Größe 4 reduzieren
        }
    }

    // ******* 6. Ziehe die Indexe der Zellen bis aufs unterste Level, die Blattzellen sind *******
    void _6_FillLeafIndexTree()
    {
        m_CurComputeShader = m_ComputeShaderList[5];
        int kernelID = m_CurComputeShader.FindKernel("main");

        m_CurComputeShader.SetBuffer(kernelID, "typeTree", m_TypeTree_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "leafIndexTree", m_LeafIndexTree_Buffer);

        m_CurComputeShader.SetInts("treeSizeInLevel", Int4ArrayTo1DArray(m_FillCounterTreesData.treeSizeInLevel));


        int _3DGroupCount, curStartLevel, curLevelResolution, firstDispatchLoops, curLoops;
        bool firstStep = true;
        // berechne curLoops, für den ersten Dispatch ist das wichtig!
        firstDispatchLoops = Constants.SUBDIVS % 4;
        if (firstDispatchLoops == 0)
            firstDispatchLoops = 4;

        // es gibt immer einen Level mehr als SUBDIVS, also wird mit dem Index SUBDIVS auf den letzten Level zugegriffen,
        // wir suchen den vorletzten Level, also SUBDIVS - 1!
        curStartLevel = 0;
        while (curStartLevel < Constants.SUBDIVS)
        {
            if (firstStep)
            {
                curLoops = firstDispatchLoops;
                firstStep = false;
            }
            else
                curLoops = 4;

            // rechne curStartLevel + 4, da die Resolution benutzt wird um zu ermitteln, wie viele Threads gespawnt werden sollen
            // es werden aber so viele Threads gebraucht, wie es Zellen im viertnächsten Level gibt
            curLevelResolution = (int)Mathf.Pow(8, curStartLevel + 3); // 8 hoch den aktuellen Level ergibt die Auflösung für den Level
                                                                 // die dritte Wurzel von der aktuellen Auflösung geteilt durch 512 ergibt die Anzahl an Gruppen die erzeugt werden müssen pro Dimension
            _3DGroupCount = (int)Mathf.Ceil(Mathf.Pow(curLevelResolution / 512.0f, 1.0f / 3.0f));

            // Constant Buffer updaten
            m_CurComputeShader.SetInt("startLevel", curStartLevel);
            m_CurComputeShader.SetInt("loops", curLoops);

            m_CurComputeShader.Dispatch(kernelID, _3DGroupCount, _3DGroupCount, _3DGroupCount);

            curStartLevel += curLoops; // der Shader kann mit 512 Threads / Gruppe die Eingabemenge um die Größe 4 reduzieren
        }
    }

    // ******* 7. Befülle Countertrees mit den Daten für jedes Objekt *******
    void _7_CellTrianglePairs()
    {
        m_CurComputeShader = m_ComputeShaderList[6];
        int kernelID = m_CurComputeShader.FindKernel("main");

        m_CurComputeShader.SetBuffer(kernelID, "boundingBoxes", m_BoundingBox_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "sceneMinPoints", m_GroupMinPoint_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "sceneMaxPoints", m_GroupMaxPoint_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "globalCounterTree", m_GlobalCounterTree_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "leafIndexTree", m_LeafIndexTree_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "cellTrianglePairs", m_CellTrianglePairs_Buffer);
        m_CellTrianglePairs_Buffer.SetCounterValue(0);

        m_CurComputeShader.SetBuffer(kernelID, "objectsLastIndices", m_ObjectsLastIndices_Buffer);

        m_CurComputeShader.SetInt("objectCount", m_ObjectCount);
        m_CurComputeShader.SetInts("treeSizeInLevel", Int4ArrayTo1DArray(m_FillCounterTreesData.treeSizeInLevel));

        int xThreadGroups = (int)Mathf.Ceil(m_TriangleCount / 1024.0f);
        m_CurComputeShader.Dispatch(kernelID, xThreadGroups, 1, 1);
    }

    // ******* 8. Sortiere cellTrianglePairs nach Zellen-IDs *******
    bool _8_SortCellTrianglePairs()
    {

        int sortIndicesCountPow2 = m_SortIndicesCount - 1; // m_SortIndices ist ja eins größer als die Zweierpotenz, sortIndicesPow2 ist die Zweierpotenz, mit der Schrittweiten und loop-Werte ebrechnet werden
        int read2BitsFromHere = 0;
        bool backBufferIsInput = false;

        while (read2BitsFromHere < 22)
        {
            // ####################################################_8_1_####################################################
            m_CurComputeShader = m_ComputeShaderList[7];
            int kernelID = m_CurComputeShader.FindKernel("main");

            if (backBufferIsInput)
                m_CurComputeShader.SetBuffer(kernelID, "cellTrianglePairs", m_CellTrianglePairsBackBuffer_Buffer);
            else
                m_CurComputeShader.SetBuffer(kernelID, "cellTrianglePairs", m_CellTrianglePairs_Buffer);

            m_CurComputeShader.SetBuffer(kernelID, "sortIndices", m_SortIndices_Buffer);


            int _1_curInputSize = sortIndicesCountPow2;
            int _1_curWorkSize = _1_curInputSize / 2;
            int _1_curLoops;
            int _1_combineDistance = 1;
            bool readFromInput = true;
            int curRead2BitsFromHere; // hier wird entweder read2BitsFromHere eingetragen oder -1, wenn es der erste Durchgang ist
            int groupCount = 0;
            while (_1_curWorkSize > 2)
            {
                groupCount = (int)Mathf.Ceil(_1_curWorkSize / 1024.0f);
                if (_1_curWorkSize >= 1024)
                    _1_curLoops = 11; // 11 Druchläufe verkleinern 1024 auf 1, wir wollen lediglich im letzten Durchlauf auf 2 verkleinern, wichtiger Unterschied!, also hier 11 statt 10
                else
                    _1_curLoops = (int)Log2(_1_curInputSize) - 1; // weil bei 2 Elementen aufgehört werden kann, laufe einmal weniger (ansonsten wäre es bis 1 Element gegangen)
                if (!readFromInput) // sollte es der erste Durchlauf sein, müssen die Bits eingelesen werden, dem Shader wird curRead2BitsFromHere = -1 übergeben
                    curRead2BitsFromHere = -1;
                else // ansonsten wurden die Bits schon eingelesen und die relevanten read2BitsFromHere werden an den Shader übergeben
                    curRead2BitsFromHere = read2BitsFromHere;

                m_CurComputeShader.SetInt("loops", _1_curLoops);
                m_CurComputeShader.SetInt("read2BitsFromHere", curRead2BitsFromHere);
                m_CurComputeShader.SetInt("startCombineDistance", _1_combineDistance);

                m_CurComputeShader.Dispatch(kernelID, groupCount, 1, 1);

                _1_curInputSize /= 2048;
                _1_curWorkSize = _1_curInputSize / 2;
                _1_combineDistance *= 2048;
                readFromInput = false;
            }

            // ####################################################_8_2_####################################################

            // Phase 2 der exklusive Prefix Summe
            m_CurComputeShader = m_ComputeShaderList[8];
            kernelID = m_CurComputeShader.FindKernel("main");

            m_CurComputeShader.SetBuffer(kernelID, "sortIndices", m_SortIndices_Buffer);

            if (backBufferIsInput)
                m_CurComputeShader.SetBuffer(kernelID, "cellTrianglePairs", m_CellTrianglePairsBackBuffer_Buffer);
            else
                m_CurComputeShader.SetBuffer(kernelID, "cellTrianglePairs", m_CellTrianglePairs_Buffer);

            ulong _2_curInputSize;
            int _2_curLoops, _2_curThreadDistance/*, _2_curStartCombineDistance*/;

            // ermittle die InputSize des ersten Dispatches
            _2_curInputSize = (ulong)sortIndicesCountPow2;
            while (_2_curInputSize > 2048) // teile solange durch 2048, bis ein Wert kleiner als 2048 herauskommt, das ist die inputSize für den ersten Dispatch
            {
                _2_curInputSize /= 2048;
            }
            int _2_curWorkSize = (int)_2_curInputSize / 2;
            _2_curThreadDistance = sortIndicesCountPow2 / _2_curWorkSize; // * 2, weil ein Thread ja am Ende 2 Inputs bearbeitet, die Distanz ist also doppelt so groß
            _2_curLoops = (int)Log2((int)_2_curInputSize);// curInputSize wird nicht durch 2 geteilt, da log2 ja die Basis 2 hat, wir aber am Ende auf 1 kommen wollen, also das Ergebnis nochmal durch 2 teilen
            bool firstStep = true;
            while (_2_curInputSize <= (uint)sortIndicesCountPow2) // beim letzten Schritt ist die inputSize = m_SortIndicesCount, deswegen das <=
            {
                groupCount = (int)Mathf.Ceil(_2_curWorkSize / 1024.0f);

                int iFirstStep = 0;
                if (firstStep) iFirstStep = 1;
                m_CurComputeShader.SetInt("bool_firstStep", iFirstStep);
                m_CurComputeShader.SetInt("threadDistance", _2_curThreadDistance);
                m_CurComputeShader.SetInt("loops", _2_curLoops);
                m_CurComputeShader.SetInt("read2BitsFromHere", read2BitsFromHere);

                m_CurComputeShader.Dispatch(kernelID, groupCount, 1, 1);

                _2_curInputSize *= 2048;
                _2_curWorkSize = (int)_2_curInputSize / 2;
                _2_curThreadDistance /= 2048;
                _2_curLoops = 11; // ab dem ersten Schritt werden immer 11 Schritte (soviel kann eine Gruppe reduzieren) ausgeführt
                                  //_2_curStartCombineDistance /= (UINT)pow (2, _2_curLoops);
                firstStep = false;
            }

            // ####################################################_8_3_####################################################
            // sortiere mit Hilfe der exklusiven Prefix-Summen
            m_CurComputeShader = m_ComputeShaderList[9];
            kernelID = m_CurComputeShader.FindKernel("main");

            m_CurComputeShader.SetBuffer(kernelID, "sortIndices", m_SortIndices_Buffer);

            if (backBufferIsInput)
            {
                m_CurComputeShader.SetBuffer(kernelID, "cellTrianglePairsInput", m_CellTrianglePairsBackBuffer_Buffer);
                m_CurComputeShader.SetBuffer(kernelID, "cellTrianglePairsOutput", m_CellTrianglePairs_Buffer);
            }
            else
            {
                m_CurComputeShader.SetBuffer(kernelID, "cellTrianglePairsInput", m_CellTrianglePairs_Buffer);
                m_CurComputeShader.SetBuffer(kernelID, "cellTrianglePairsOutput", m_CellTrianglePairsBackBuffer_Buffer);
            }

            groupCount = (int)Mathf.Ceil(m_CellTrianglePairsCount / 1024.0f);
            m_CurComputeShader.Dispatch(kernelID, groupCount, 1, 1);

            backBufferIsInput = !backBufferIsInput;
            read2BitsFromHere += 2;
        }

        return backBufferIsInput;
    }

    // ******* 9. Finde im sortierten cellTrianglePairsBuffer alle Dreieckspaare, deren Bounding Boxes sich überschneiden *******
    void _9_FindTrianglePairs(bool backBufferIsInput)
    {
        m_CurComputeShader = m_ComputeShaderList[10];
        int kernelID = m_CurComputeShader.FindKernel("main");

        if (backBufferIsInput)
            m_CurComputeShader.SetBuffer(kernelID, "cellTrianglePairs", m_CellTrianglePairsBackBuffer_Buffer);

        else
            m_CurComputeShader.SetBuffer(kernelID, "cellTrianglePairs", m_CellTrianglePairs_Buffer);

        m_CurComputeShader.SetBuffer(kernelID, "boundingBoxes", m_BoundingBox_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "trianglePairs", m_TrianglePairs_Buffer);
        m_TrianglePairs_Buffer.SetCounterValue(0);


        int groupCount = (int)Mathf.Ceil(m_TrianglePairsCount / 1024.0f);
        m_CurComputeShader.Dispatch(kernelID, groupCount, 1, 1);

        m_CellTrianglePairs_Buffer.SetData(m_CellTrianglePairs_Zero);
        m_CellTrianglePairsBackBuffer_Buffer.SetData(m_CellTrianglePairs_Zero);
    }

    // ******* 10. Überprüfe alle Dreiecke in Triangle-Pairs, ob sie sich tatsächlich überschneiden  *******
    void _10_TriangleIntersections()
    {
        m_CurComputeShader = m_ComputeShaderList[11];
        int kernelID = m_CurComputeShader.FindKernel("main");

        m_CurComputeShader.SetBuffer(kernelID, "vertexBuffer", m_Vertex_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "triangleBuffer", m_Triangle_Buffer);

        m_CurComputeShader.SetBuffer(kernelID, "trianglePairs", m_TrianglePairs_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "intersectingObjects", m_IntersectingObjects_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "intersectCenters", m_IntersectCenters_Buffer);
        m_IntersectCenters_Buffer.SetCounterValue(0);

        int groupCount = (int)Mathf.Ceil(m_TrianglePairsCount / 1024.0f);
        m_CurComputeShader.Dispatch(kernelID, groupCount, 1, 1);
    }

    // ******* 11. Überschreibe den Ergebnis-Buffer mit 0en  *******
    void _11_ZeroIntersectionCenters()
    {
        m_CurComputeShader = m_ComputeShaderList[12];
        int kernelID = m_CurComputeShader.FindKernel("main");

        m_CurComputeShader.SetBuffer(kernelID, "intersectCenters", m_IntersectCenters_Buffer);

        int groupCount = (int)Mathf.Ceil(m_TrianglePairsCount / 1024.0f);
        m_CurComputeShader.Dispatch(kernelID, groupCount, 1, 1);
    }

    void _10_TriangleIntersections_GetFinalResult()
    {
        m_IntersectingObjects_Buffer.GetData(m_Results10_1_IntersectingObjects);
        m_IntersectCenters_Buffer.GetData(m_Results10_2_IntersectionPoints);
    }

    // führe die Kollisionsberechnung für das aktuelle Frame durch
    void Frame()
    {
        //auto begin = high_resolution_clock::now();

        //auto end = high_resolution_clock::now();
        //cout << "Buffer created" << ": " << duration_cast<milliseconds>(end - begin).count() << "ms" << endl;

        _1_BoundingBoxes();

        _2_SceneCoundingBox();

        _3_FillCounterTrees();

        _4_GlobalCounterTree();

        _5_FillTypeTree();

        _6_FillLeafIndexTree();

        _7_CellTrianglePairs();

        bool backBufferIsInput = _8_SortCellTrianglePairs();

        _9_FindTrianglePairs(backBufferIsInput);

        _10_TriangleIntersections();
        _10_TriangleIntersections_GetFinalResult();

        _11_ZeroIntersectionCenters();

        m_CopyTo1 = !m_CopyTo1;
    }
}
