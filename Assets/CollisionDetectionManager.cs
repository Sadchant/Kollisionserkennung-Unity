using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;


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


        deviceContext->CSSetUnorderedAccessViews(0, 1, &m_GlobalCounterTree_UAV, 0);
        deviceContext->CSSetUnorderedAccessViews(1, 1, &m_TypeTree_UAV, 0);
        deviceContext->CSSetConstantBuffers(0, 1, &m_TreeSizeInLevel_CBuffer);


        int _3DGroupCount, curStartLevel, curLevelResolution;
        // es gibt immer einen Level mehr als SUBDIVS, also wird mit dem Index SUBDIVS auf den letzten Level zugegriffen,
        // wir suchen den vorletzten Level, also SUBDIVS - 1!
        curStartLevel = SUBDIVS - 1;
        while (curStartLevel >= 0)
        {
            curLevelResolution = (int)pow(8, curStartLevel); // 8 hoch den aktuellen Level ergibt die Auflösung für den Level
                                                             // die dritte Wurzel von der aktuellen Auflösung geteilt durch 512 ergibt die Anzahl an Gruppen die erzeugt werden müssen pro Dimension
            _3DGroupCount = (int)ceil(pow(curLevelResolution / 512.0, 1.0 / 3.0));

            // Constant Buffer updaten
            SingleUINT s_StartLevel = { (UINT)curStartLevel };
            deviceContext->UpdateSubresource(m_StartLevel_CBuffer, 0, NULL, &s_StartLevel, 0, 0);
            deviceContext->CSSetConstantBuffers(1, 1, &m_StartLevel_CBuffer);

            deviceContext->Dispatch(_3DGroupCount, _3DGroupCount, _3DGroupCount);

            curStartLevel -= 4; // der Shader kann mit 512 Threads / Gruppe die Eingabemenge um die Größe 4 reduzieren
        }
        deviceContext->CSSetUnorderedAccessViews(1, 1, &m_NULL_UAV, 0);

    }

    // ******* 6. Ziehe die Indexe der Zellen bis aufs unterste Level, die Blattzellen sind *******
    void _6_FillLeafIndexTree()
    {
        m_curComputeShader = m_ComputeShaderVector[5];
        deviceContext->CSSetShader(m_curComputeShader, NULL, 0);

        deviceContext->CSSetUnorderedAccessViews(0, 1, &m_TypeTree_UAV, 0);
        deviceContext->CSSetUnorderedAccessViews(1, 1, &m_LeafIndexTree_UAV, 0);
        deviceContext->CSSetConstantBuffers(0, 1, &m_TreeSizeInLevel_CBuffer);


        int _3DGroupCount, curStartLevel, curLevelResolution, firstDispatchLoops, curLoops;
        bool firstStep = true;
        // berechne curLoops, für den ersten Dispatch ist das wichtig!
        firstDispatchLoops = SUBDIVS % 4;
        if (firstDispatchLoops == 0)
            firstDispatchLoops = 4;

        // es gibt immer einen Level mehr als SUBDIVS, also wird mit dem Index SUBDIVS auf den letzten Level zugegriffen,
        // wir suchen den vorletzten Level, also SUBDIVS - 1!
        curStartLevel = 0;
        while (curStartLevel < SUBDIVS)
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
            curLevelResolution = (int)pow(8, curStartLevel + 3); // 8 hoch den aktuellen Level ergibt die Auflösung für den Level
                                                                 // die dritte Wurzel von der aktuellen Auflösung geteilt durch 512 ergibt die Anzahl an Gruppen die erzeugt werden müssen pro Dimension
            _3DGroupCount = (int)ceil(pow(curLevelResolution / 512.0, 1.0 / 3.0));

            // Constant Buffer updaten
            SingleUINT s_StartLevel = { (UINT)curStartLevel };
            SingleUINT s_Loops = { (UINT)curLoops };
            deviceContext->UpdateSubresource(m_StartLevel_CBuffer, 0, NULL, &s_StartLevel, 0, 0);
            deviceContext->UpdateSubresource(m_Loops_CBuffer, 0, NULL, &s_Loops, 0, 0);
            deviceContext->CSSetConstantBuffers(1, 1, &m_StartLevel_CBuffer);
            deviceContext->CSSetConstantBuffers(2, 1, &m_Loops_CBuffer);

            deviceContext->Dispatch(_3DGroupCount, _3DGroupCount, _3DGroupCount);

            curStartLevel += curLoops; // der Shader kann mit 512 Threads / Gruppe die Eingabemenge um die Größe 4 reduzieren
        }
    }

    // ******* 7. Befülle Countertrees mit den Daten für jedes Objekt *******
    void _7_CellTrianglePairs()
    {
        m_curComputeShader = m_ComputeShaderVector[6];
        deviceContext->CSSetShader(m_curComputeShader, NULL, 0);

        UINT zero = 0;
        deviceContext->CSSetUnorderedAccessViews(0, 1, &m_BoundingBoxes_UAV, 0);
        deviceContext->CSSetUnorderedAccessViews(1, 1, &m_GroupMinPoints_UAV, 0);
        deviceContext->CSSetUnorderedAccessViews(2, 1, &m_GroupMaxPoints_UAV, 0);
        deviceContext->CSSetUnorderedAccessViews(3, 1, &m_GlobalCounterTree_UAV, 0);
        deviceContext->CSSetUnorderedAccessViews(4, 1, &m_LeafIndexTree_UAV, 0);

        deviceContext->CSSetUnorderedAccessViews(5, 1, &m_CellTrianglePairs_UAV, &zero);

        deviceContext->CSSetShaderResources(0, 1, &m_ObjectsLastIndices_SRV);

        deviceContext->CSSetConstantBuffers(0, 1, &m_ObjectCount_CBuffer);
        deviceContext->CSSetConstantBuffers(1, 1, &m_TreeSizeInLevel_CBuffer);

        int xThreadGroups = (int)ceil(m_TriangleCount / 1024.0f);
        deviceContext->Dispatch(xThreadGroups, 1, 1);

        // entferne die UAVs wieder von den Slots 0 - 2, damit sie wieder verwendet werden können
        deviceContext->CSSetUnorderedAccessViews(0, 1, &m_NULL_UAV, 0);
        deviceContext->CSSetUnorderedAccessViews(1, 1, &m_NULL_UAV, 0);
        deviceContext->CSSetUnorderedAccessViews(2, 1, &m_NULL_UAV, 0);
        deviceContext->CSSetUnorderedAccessViews(3, 1, &m_NULL_UAV, 0);
        deviceContext->CSSetUnorderedAccessViews(4, 1, &m_NULL_UAV, 0);
        deviceContext->CSSetUnorderedAccessViews(5, 1, &m_NULL_UAV, 0);
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
            m_curComputeShader = m_ComputeShaderVector[7];
            deviceContext->CSSetShader(m_curComputeShader, NULL, 0);

            if (backBufferIsInput)
                deviceContext->CSSetUnorderedAccessViews(0, 1, &m_CellTrianglePairsBackBuffer_UAV, 0);
            else
                deviceContext->CSSetUnorderedAccessViews(0, 1, &m_CellTrianglePairs_UAV, 0);

            deviceContext->CSSetUnorderedAccessViews(1, 1, &m_SortIndices_UAV, 0);

            int _1_curInputSize = sortIndicesCountPow2;
            int _1_curWorkSize = _1_curInputSize / 2;
            UINT _1_curLoops;
            UINT _1_combineDistance = 1;
            bool readFromInput = true;
            int curRead2BitsFromHere; // hier wird entweder read2BitsFromHere eingetragen oder -1, wenn es der erste Durchgang ist
            while (_1_curWorkSize > 2)
            {
                int groupCount = (int)ceil(_1_curWorkSize / 1024.0f);
                if (_1_curWorkSize >= 1024)
                    _1_curLoops = 11; // 11 Druchläufe verkleinern 1024 auf 1, wir wollen lediglich im letzten Durchlauf auf 2 verkleinern, wichtiger Unterschied!, also hier 11 statt 10
                else
                    _1_curLoops = (UINT)log2(_1_curInputSize) - 1; // weil bei 2 Elementen aufgehört werden kann, laufe einmal weniger (ansonsten wäre es bis 1 Element gegangen)
                if (!readFromInput) // sollte es der erste Durchlauf sein, müssen die Bits eingelesen werden, dem Shader wird curRead2BitsFromHere = -1 übergeben
                    curRead2BitsFromHere = -1;
                else // ansonsten wurden die Bits schon eingelesen und die relevanten read2BitsFromHere werden an den Shader übergeben
                    curRead2BitsFromHere = read2BitsFromHere;
                RadixSort_ExclusivePrefixSumData radixSort_ExclusivePrefixSum_Data = { _1_curLoops, curRead2BitsFromHere, _1_combineDistance };
                deviceContext->UpdateSubresource(m_RadixSort_ExclusivePrefixSumData_CBuffer, 0, NULL, &radixSort_ExclusivePrefixSum_Data, 0, 0);
                deviceContext->CSSetConstantBuffers(0, 1, &m_RadixSort_ExclusivePrefixSumData_CBuffer);
                deviceContext->Dispatch(groupCount, 1, 1);
                _1_curInputSize /= 2048;
                _1_curWorkSize = _1_curInputSize / 2;
                _1_combineDistance *= 2048;
                readFromInput = false;
            }

            deviceContext->CSSetUnorderedAccessViews(0, 1, &m_NULL_UAV, 0);
            deviceContext->CSSetUnorderedAccessViews(1, 1, &m_NULL_UAV, 0);

            // ####################################################_8_2_####################################################

            // Phase 2 der exklusive Prefix Summe
            m_curComputeShader = m_ComputeShaderVector[8];
            deviceContext->CSSetShader(m_curComputeShader, NULL, 0);

            deviceContext->CSSetUnorderedAccessViews(0, 1, &m_SortIndices_UAV, 0);
            if (backBufferIsInput)
                deviceContext->CSSetUnorderedAccessViews(1, 1, &m_CellTrianglePairsBackBuffer_UAV, 0);
            else
                deviceContext->CSSetUnorderedAccessViews(1, 1, &m_CellTrianglePairs_UAV, 0);

            unsigned long long _2_curInputSize;
            UINT _2_curLoops, _2_curThreadDistance/*, _2_curStartCombineDistance*/;

            // ermittle die InputSize des ersten Dispatches
            _2_curInputSize = sortIndicesCountPow2; // DURCH 2 HÖCHSTWAHRSCHEINLICH HIER GUCKEN!!!
            while (_2_curInputSize > 2048) // teile solange durch 2048, bis ein Wert kleiner als 2048 herauskommt, das ist die inputSize für den ersten Dispatch
            {
                _2_curInputSize /= 2048;
            }
            int _2_curWorkSize = (int)_2_curInputSize / 2;
            _2_curThreadDistance = sortIndicesCountPow2 / _2_curWorkSize; // * 2, weil ein Thread ja am Ende 2 Inputs bearbeitet, die Distanz ist also doppelt so groß
            _2_curLoops = (int)log2(_2_curInputSize);// curInputSize wird nicht durch 2 geteilt, da log2 ja die Basis 2 hat, wir aber am Ende auf 1 kommen wollen, also das Ergebnis nochmal durch 2 teilen
            bool firstStep = true;
            while (_2_curInputSize <= (UINT)sortIndicesCountPow2) // beim letzten Schritt ist die inputSize = m_SortIndicesCount, deswegen das <=
            {
                int groupCount = (int)ceil(_2_curWorkSize / 1024.0f);
                RadixSort_ExclusivePrefixSumData2 radixSort_ExclusivePrefixSum_Data2 = { (UINT)firstStep, _2_curThreadDistance, _2_curLoops, read2BitsFromHere };
                deviceContext->UpdateSubresource(m_RadixSort_ExclusivePrefixSumData2_CBuffer, 0, NULL, &radixSort_ExclusivePrefixSum_Data2, 0, 0);
                deviceContext->CSSetConstantBuffers(0, 1, &m_RadixSort_ExclusivePrefixSumData2_CBuffer);
                deviceContext->Dispatch(groupCount, 1, 1);
                _2_curInputSize *= 2048;
                _2_curWorkSize = (int)_2_curInputSize / 2;
                _2_curThreadDistance /= 2048;
                _2_curLoops = 11; // ab dem ersten Schritt werden immer 11 Schritte (soviel kann eine Gruppe reduzieren) ausgeführt
                                  //_2_curStartCombineDistance /= (UINT)pow (2, _2_curLoops);
                firstStep = false;
            }

            deviceContext->CSSetUnorderedAccessViews(0, 1, &m_NULL_UAV, 0);
            deviceContext->CSSetUnorderedAccessViews(1, 1, &m_NULL_UAV, 0);

            // ####################################################_8_3_####################################################
            // sortiere mit Hilfe der exklusiven Prefix-Summen
            m_curComputeShader = m_ComputeShaderVector[9];
            deviceContext->CSSetShader(m_curComputeShader, NULL, 0);

            deviceContext->CSSetUnorderedAccessViews(0, 1, &m_SortIndices_UAV, 0);
            if (backBufferIsInput)
            {
                deviceContext->CSSetUnorderedAccessViews(1, 1, &m_CellTrianglePairsBackBuffer_UAV, 0);
                deviceContext->CSSetUnorderedAccessViews(2, 1, &m_CellTrianglePairs_UAV, 0);
            }
            else
            {
                deviceContext->CSSetUnorderedAccessViews(1, 1, &m_CellTrianglePairs_UAV, 0);
                deviceContext->CSSetUnorderedAccessViews(2, 1, &m_CellTrianglePairsBackBuffer_UAV, 0);
            }

            int groupCount = (int)ceil(m_CellTrianglePairsCount / 1024.0f);
            deviceContext->Dispatch(groupCount, 1, 1);

            deviceContext->CSSetUnorderedAccessViews(0, 1, &m_NULL_UAV, 0);
            deviceContext->CSSetUnorderedAccessViews(1, 1, &m_NULL_UAV, 0);
            deviceContext->CSSetUnorderedAccessViews(2, 1, &m_NULL_UAV, 0);

            backBufferIsInput = !backBufferIsInput;
            read2BitsFromHere += 2;
        }

        return backBufferIsInput;
    }

    // ******* 9. Finde im sortierten cellTrianglePairsBuffer alle Dreieckspaare, deren Bounding Boxes sich überschneiden *******
    void _9_FindTrianglePairs(bool backBufferIsInput)
    {
        m_curComputeShader = m_ComputeShaderVector[10];
        deviceContext->CSSetShader(m_curComputeShader, NULL, 0);

        UINT zero = 0;

        if (backBufferIsInput)
            deviceContext->CSSetUnorderedAccessViews(0, 1, &m_CellTrianglePairsBackBuffer_UAV, 0);
        else
            deviceContext->CSSetUnorderedAccessViews(0, 1, &m_CellTrianglePairs_UAV, 0);

        deviceContext->CSSetUnorderedAccessViews(1, 1, &m_BoundingBoxes_UAV, 0);
        deviceContext->CSSetUnorderedAccessViews(2, 1, &m_TrianglePairs_UAV, &zero); // setze den Buffer-Counter auf 0 zurück


        int groupCount = (int)ceil(m_TrianglePairsCount / 1024.0);
        deviceContext->Dispatch(groupCount, 1, 1);

        deviceContext->CSSetUnorderedAccessViews(0, 1, &m_NULL_UAV, 0);
        deviceContext->CSSetUnorderedAccessViews(1, 1, &m_NULL_UAV, 0);
        deviceContext->CSSetUnorderedAccessViews(2, 1, &m_NULL_UAV, 0);


        deviceContext->UpdateSubresource(m_CellTrianglePairs_Buffer, 0, NULL, m_CellTrianglePairs_Zero, m_CellTrianglePairsCount * sizeof(CellTrianglePair), 0);
        deviceContext->UpdateSubresource(m_CellTrianglePairsBackBuffer_Buffer, 0, NULL, m_CellTrianglePairs_Zero, m_CellTrianglePairsCount * sizeof(CellTrianglePair), 0);
        //Arguments: The buffer, The subresource (0), A destination box(NULL), The data to write to the buffer, the size of the buffer, the depth of the buffer
    }

    // ******* 10. Überprüfe alle Dreiecke in Triangle-Pairs, ob sie sich tatsächlich überschneiden  *******
    void _10_TriangleIntersections()
    {
        m_curComputeShader = m_ComputeShaderVector[11];
        deviceContext->CSSetShader(m_curComputeShader, NULL, 0);

        deviceContext->CSSetShaderResources(0, 1, &m_Vertices_SRV);
        deviceContext->CSSetShaderResources(1, 1, &m_Triangles_SRV);

        UINT zero = 0;

        //deviceContext->CSSetUnorderedAccessViews(0, 1, &m_CellTrianglePairs_UAV, 0);
        deviceContext->CSSetUnorderedAccessViews(0, 1, &m_TrianglePairs_UAV, 0);
        deviceContext->CSSetUnorderedAccessViews(1, 1, &m_IntersectingObjects_UAV, 0);
        deviceContext->CSSetUnorderedAccessViews(2, 1, &m_IntersectCenters_UAV, &zero); // setzte den Buffer-internen Counter auf 0 zurück

        int groupCount = (int)ceil(m_TrianglePairsCount / 1024.0);
        deviceContext->Dispatch(groupCount, 1, 1);

        deviceContext->CSSetUnorderedAccessViews(0, 1, &m_NULL_UAV, 0);
        deviceContext->CSSetUnorderedAccessViews(1, 1, &m_NULL_UAV, 0);
        deviceContext->CSSetUnorderedAccessViews(2, 1, &m_NULL_UAV, 0);

    }

    // ******* 11. Überschreibe den Ergebnis-Buffer mit 0en  *******
    void _11_ZeroIntersectionCenters()
    {
        m_curComputeShader = m_ComputeShaderVector[12];
        deviceContext->CSSetShader(m_curComputeShader, NULL, 0);

        deviceContext->CSSetUnorderedAccessViews(0, 1, &m_IntersectCenters_UAV, 0); // setzte den Buffer-internen Counter auf 0 zurück

        int groupCount = (int)ceil(m_TrianglePairsCount / 1024.0);
        deviceContext->Dispatch(groupCount, 1, 1);

        deviceContext->CSSetUnorderedAccessViews(0, 1, &m_NULL_UAV, 0);
    }

    void _10_TriangleIntersections_GetFinalResult()
    {
        SAFEDELETEARRAY(m_Results10_1_IntersectingObjects);
        m_Results10_1_IntersectingObjects = new UINT[m_ObjectCount];
        D3D11_MAPPED_SUBRESOURCE MappedResource10_1 = { 0 };
        deviceContext->CopyResource(m_IntersectingObjects_Result_Buffer, m_IntersectingObjects_Buffer);
        deviceContext->Map(m_IntersectingObjects_Result_Buffer, 0, D3D11_MAP_READ, 0, &MappedResource10_1);
        _Analysis_assume_(MappedResource10_1.pData);
        assert(MappedResource10_1.pData);
        memcpy(m_Results10_1_IntersectingObjects, MappedResource10_1.pData, m_ObjectCount * sizeof(UINT));
        deviceContext->Unmap(m_IntersectingObjects_Result_Buffer, 0);


        D3D11_MAPPED_SUBRESOURCE MappedResource10_2 = { 0 };

        if (m_CopyTo1)
        {
            deviceContext->CopyResource(m_IntersectCenters_Result1_Buffer, m_IntersectCenters_Buffer);
            deviceContext->Map(m_IntersectCenters_Result2_Buffer, 0, D3D11_MAP_READ, 0, &MappedResource10_2);
        }
        else
        {
            deviceContext->CopyResource(m_IntersectCenters_Result2_Buffer, m_IntersectCenters_Buffer);
            deviceContext->Map(m_IntersectCenters_Result1_Buffer, 0, D3D11_MAP_READ, 0, &MappedResource10_2);
        }

        _Analysis_assume_(MappedResource10_2.pData);
        assert(MappedResource10_2.pData);
        m_Results10_2_IntersectionPoints = (Vertex*)MappedResource10_2.pData;
        if (m_CopyTo1)
            deviceContext->Unmap(m_IntersectCenters_Result2_Buffer, 0);
        else
            deviceContext->Unmap(m_IntersectCenters_Result1_Buffer, 0);


        /*int i = 0;
        for (i = 0; i < m_IntersectionCentersCount; i++)
        {
            if (m_Results10_2_IntersectionPoints[i].x == 0 && m_Results10_2_IntersectionPoints[i].y == 0 && m_Results10_2_IntersectionPoints[i].z == 0)
                break;
        }
        cout << "intersectionPoints : " << i << endl;*/


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

    public void FrameOld()
    {
        
        

        
        // solange mehr als eine Gruppe gestartet werden muss, werden die Min-MaxPoints nicht auf ein Ergebnis reduziert sein,
        // da es ja immer ein Ergebnis pro Gruppe berechnet wird


        ////####### Daten von der GPU kopieren #######
        //SAFEDELETEARRAY(m_Results2_1);
        //SAFEDELETEARRAY(m_Results2_2);

        //m_Results2_1 = new Vertex[m_GroupResult_Count];
        //m_Results2_2 = new Vertex[m_GroupResult_Count];

        //D3D11_MAPPED_SUBRESOURCE MappedResource1 = { 0 };
        //D3D11_MAPPED_SUBRESOURCE MappedResource2 = { 0 };
        //deviceContext->CopyResource(m_Result_Buffer2_1, m_GroupMinPoint_Buffer);
        //deviceContext->CopyResource(m_Result_Buffer2_2, m_GroupMaxPoint_Buffer);
        //HRESULT result = deviceContext->Map(m_Result_Buffer2_1, 0, D3D11_MAP_READ, 0, &MappedResource1);
        //result = deviceContext->Map(m_Result_Buffer2_2, 0, D3D11_MAP_READ, 0, &MappedResource2);

        //RETURN_FALSE_IF_FAIL(result);

        //_Analysis_assume_(MappedResource1.pData);
        //assert(MappedResource1.pData);

        //_Analysis_assume_(MappedResource2.pData);
        //assert(MappedResource2.pData);
        //// m_BoundingBoxes wird in CreateVertexAndTriangleArray neu initialisiert
        //memcpy(m_Results2_1, MappedResource1.pData, m_GroupResult_Count * sizeof(Vertex));
        //memcpy(m_Results2_2, MappedResource2.pData, m_GroupResult_Count * sizeof(Vertex));
        //deviceContext->Unmap(m_Result_Buffer2_1, 0);
        //deviceContext->Unmap(m_Result_Buffer2_2, 0);

        //end = high_resolution_clock::now();
        ////cout << "Shader 1 + 2 (+ copy-back): " << duration_cast<milliseconds>(end - begin).count() << "ms" << endl;
        //begin = high_resolution_clock::now();


        // ####### Befülle Countertrees mit den Daten für jedes Objekt #######

        

        //// entferne die UAVs wieder von den Slots 0 - 2, damit sie wieder verwendet werden können
        //deviceContext->CSSetUnorderedAccessViews(0, 1, &m_NULL_UAV, 0);
        //deviceContext->CSSetUnorderedAccessViews(1, 1, &m_NULL_UAV, 0);
        //deviceContext->CSSetUnorderedAccessViews(2, 1, &m_NULL_UAV, 0);
        //deviceContext->CSSetUnorderedAccessViews(3, 1, &m_NULL_UAV, 0);

        ////####### Daten von der GPU kopieren #######
        ////SAFEDELETEARRAY(m_Results3);
        ////m_Results3 = new UINT[m_CounterTreesSize];
        ////D3D11_MAPPED_SUBRESOURCE MappedResource3 = { 0 };
        ////deviceContext->CopyResource(m_Result_Buffer3, m_CounterTrees_Buffer);
        ////HRESULT result = deviceContext->Map(m_Result_Buffer3, 0, D3D11_MAP_READ, 0, &MappedResource3);
        ////RETURN_FALSE_IF_FAIL(result);
        ////_Analysis_assume_(MappedResource3.pData);
        ////assert(MappedResource3.pData);
        ////// m_BoundingBoxes wird in CreateVertexAndTriangleArray neu initialisiert
        ////memcpy(m_Results3, MappedResource3.pData, m_CounterTreesSize * sizeof(UINT));
        ////deviceContext->Unmap(m_Result_Buffer3, 0);
        ////####### Daten von der GPU kopieren #######

        //// ####### Befülle den GlobalCounterTree mit den Daten, wie viele Überschneidungstets es pro Zelle gibt #######

        //m_curComputeShader = m_ComputeShaderVector[3];
        //deviceContext->CSSetShader(m_curComputeShader, NULL, 0);

        //deviceContext->CSSetUnorderedAccessViews(0, 1, &m_CounterTrees_UAV, 0);
        //deviceContext->CSSetUnorderedAccessViews(1, 1, &m_GlobalCounterTree_UAV, 0);
        //deviceContext->CSSetConstantBuffers(0, 1, &m_FillCounterTreesData_CBuffer);
        //xThreadGroups = (int)ceil(m_TreeSize / 1024.0f);
        //deviceContext->Dispatch(xThreadGroups, 1, 1);

        ////####### Daten von der GPU kopieren #######
        //SAFEDELETEARRAY(m_Results4);
        //m_Results4 = new UINT[m_TreeSize];
        //D3D11_MAPPED_SUBRESOURCE MappedResource4 = { 0 };
        //deviceContext->CopyResource(m_Result_Buffer4, m_GlobalCounterTree_Buffer);
        //HRESULT result = deviceContext->Map(m_Result_Buffer4, 0, D3D11_MAP_READ, 0, &MappedResource4);
        //RETURN_FALSE_IF_FAIL(result);
        //_Analysis_assume_(MappedResource4.pData);
        //assert(MappedResource4.pData);
        //// m_BoundingBoxes wird in CreateVertexAndTriangleArray neu initialisiert
        //memcpy(m_Results4, MappedResource4.pData, m_TreeSize * sizeof(UINT));
        //deviceContext->Unmap(m_Result_Buffer4, 0);
        ////####### Daten von der GPU kopieren #######
    }

}
