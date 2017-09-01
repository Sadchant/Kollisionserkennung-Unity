using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

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


public class CollisionDetectionManager : MonoBehaviour {

    private List<GameObject> sceneGameObjects;

    private int m_VertexCount;
    private int m_TriangleCount;
    private int m_ObjectCount;
    private int m_GroupResult_Count; // wie groß ist das Ergebnis nach einem Reduce von Buffern der Größe m_VertexCount
    private int m_TreeSize;
    private int m_CounterTreesSize;

    List<ComputeShader> m_ComputeShaderList;
    private ComputeShader m_CurComputeShader;

    private Vector3[] m_Vertices;
    private int[] m_Triangles;
    private uint[] m_ObjectsLastIndices;

    private ComputeBuffer m_Vertex_Buffer; // alle Punkte der Szene, deren Objekte kollidieren
    private ComputeBuffer m_Triangle_Buffer; // alle Dreiecke der Szene, deren Objekte kollidieren
    private ComputeBuffer m_ObjectsLastIndices_Buffer; // die Indices im Dreieck-Buffer, die das letzte Dreieck eines Objektes markieren
    private ComputeBuffer m_BoundingBox_Buffer; // die Bounding Boxes für jedes Dreieck
    private ComputeBuffer m_GroupMinPoint_Buffer; // Ergebnisbuffer einer Reduktion: beinhaltet nach einem Durchlauf die MinimalPunkte, die jede Gruppe berechnet hat
    private ComputeBuffer m_GroupMaxPoint_Buffer; // das selbe für die MaximalPunkte
    private ComputeBuffer m_CounterTrees_Buffer; // die Countertrees für alle Objekte
    private ComputeBuffer m_GlobalCounterTree_Buffer; // die Countertrees für alle Objekte
    private ComputeBuffer m_TypeTree_Buffer; // der Typetree für den globalen Tree

    FillCounterTreesData m_FillCounterTreesData;


    public void AddObject(GameObject newObject)
    {
        sceneGameObjects.Add(newObject);
        CreateVertexAndTriangleArray();
    }

    public void RemoveObject(GameObject deleteThisObject)
    {
        sceneGameObjects.Remove(deleteThisObject);
        CreateVertexAndTriangleArray();
    }

    void InitComputeShaderList()
    {
        m_ComputeShaderList = new List<ComputeShader>();
        m_ComputeShaderList.Add(Resources.Load<ComputeShader>("ComputeShader/1_BoundingBoxes"));
        m_ComputeShaderList.Add(Resources.Load<ComputeShader>("ComputeShader/2_SceneBoundingBox"));
        m_ComputeShaderList.Add(Resources.Load<ComputeShader>("ComputeShader/3_FillCounterTrees"));
        m_ComputeShaderList.Add(Resources.Load<ComputeShader>("ComputeShader/4_GlobalCounterTree"));
        m_ComputeShaderList.Add(Resources.Load<ComputeShader>("ComputeShader/5_FillTypeTree"));
    }

    // erzeugt die Arrays, die zum Initialisieren der Buffer benötigt werden
    private void CreateVertexAndTriangleArray()
    {
        //Zuerst ausrechnen, wie groß das Triangle- und das Vertex-Array sein sollte
        m_VertexCount = 0;
        foreach (GameObject gameObject in sceneGameObjects)
        {
            m_VertexCount += gameObject.GetComponent<Mesh>().vertexCount;
            m_TriangleCount += gameObject.GetComponent<Mesh>().triangles.Length;
        }

        m_ObjectCount = sceneGameObjects.Count;

        m_Vertices = new Vector3[m_VertexCount];
        m_Triangles = new int[m_TriangleCount];
        m_ObjectsLastIndices = new uint[m_ObjectCount]; // es gibt so viele Einträge wie Objekte

        int curAllVerticesCount = 0; // zähle alle VertexCounts für jedes Objekt zusammen
        int curAllIndicesCount = 0; // zähle alle IndexCounts für jedes Objekt zusammen
        // gehe über alle Objekte
        for (int i = 0; i < m_ObjectCount; i++)
        {
            Mesh curModel = sceneGameObjects[i].GetComponent<Mesh>();
            int curIndexCount = curModel.triangles.Length;
            int curVertexCount = curModel.vertexCount;
            int[] curIndexArray = (int[])curModel.triangles.Clone();
            // gehe über alle Vertices und kopiere die Vertices dieses Objekts in den Szenen-Vertexbuffer
            for (int j = 0; j < curVertexCount; j++) // iteriere über jeden Vertex in modelData
            {
                m_Vertices[curAllVerticesCount + j] = curModel.vertices[j];
            }
            // gehe über alle Indices und kopiere modifizierte Indices in den Szenen-Indexbuffer
            for (int k = 0; k < curIndexCount; k++)
            {
                // addiere die Menge der Vertices aller bisherigen Objekte, da die Indices auf den zusammenkopierten Vertexbuffer verweisen
                m_Triangles[curAllIndicesCount + k] = curModel.triangles[k] + curAllVerticesCount; 
            }
            // schreibe außerdem den letzten Index des Objektes in m_ObjectLastIndices
            m_ObjectsLastIndices[i] = (uint)curAllIndicesCount-1;

            curAllVerticesCount += curVertexCount;
            curAllIndicesCount += curIndexCount;
        }
    }

    // gib alle Buffer, Shader Resource Views und Unordered Access Views frei
    void ReleaseBuffersAndViews()
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
    }

    void CreateSceneBuffers()
    {
        // Buffer, ShaderResourceViews und UnorderedAccessViews müssen released werden (falls etwas in ihnen ist), bevor sie neu created werden!
        ReleaseBuffersAndViews();
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
    }

    private int[] _2DArrayTo1DArray(int[][] _2DArray)
    {
        int[] resultArray = new int[_2DArray.GetLength(0)* _2DArray.GetLength(1)];
        int x = _2DArray.GetLength(0) * _2DArray.GetLength(1);
        int y = _2DArray.Length;

        return resultArray;
    }

    private bool Frame()
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


        // ####### Berechne Bounding Box für die gesamte Szene #######
        m_CurComputeShader = m_ComputeShaderList[1];
        kernelID = m_CurComputeShader.FindKernel("main");

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

        m_CurComputeShader = m_ComputeShaderList[2];
        kernelID = m_CurComputeShader.FindKernel("main");

        m_CurComputeShader.SetBuffer(kernelID, "sceneMinPoints", m_GroupMinPoint_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "sceneMaxPoints", m_GroupMaxPoint_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "boundingBoxBuffer", m_BoundingBox_Buffer);
        m_CurComputeShader.SetBuffer(kernelID, "counterTrees", m_CounterTrees_Buffer);

        m_CurComputeShader.SetBuffer(kernelID, "objectsLastIndices", m_ObjectsLastIndices_Buffer);

        m_CurComputeShader.SetInts("objectCount", m_FillCounterTreesData.objectCount);
        m_CurComputeShader.SetInts("treeSizeInLevel", _2DArrayTo1DArray(m_FillCounterTreesData.treeSizeInLevel));


        //deviceContext->CSSetConstantBuffers(0, 1, &m_FillCounterTreesData_CBuffer);
        //xThreadGroups = (int)ceil(m_TriangleCount / 1024.0f);
        //deviceContext->Dispatch(xThreadGroups, 1, 1);

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


        return true;
    }


    // Use this for initialization
    void Start () {
        InitComputeShaderList();
        CreateSceneBuffers();
        _2DArrayTo1DArray(m_FillCounterTreesData.treeSizeInLevel);

    }
	
	// Update is called once per frame
	void Update () {
        Frame();

    }
}
