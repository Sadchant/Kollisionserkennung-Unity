#ifndef COMPUTE_SHADER_GLOBALS
#define COMPUTE_SHADER_GLOBALS


#define LINEAR_XTHREADS 1024
#define LINEAR_YTHREADS 1
#define LINEAR_ZTHREADS 1

#define _5_FILLTYPETREE_XTHREADS 8
#define _5_FILLTYPETREE_YTHREADS 8
#define _5_FILLTYPETREE_ZTHREADS 8

#define _6_FILLLEAFINDEXTREE_XTHREADS 8
#define _6_FILLLEAFINDEXTREE_YTHREADS 8
#define _6_FILLLEAFINDEXTREE_ZTHREADS 8

#define _10_TRIANGLEINTERSECTIONS_XTHREADS 16
#define _10_TRIANGLEINTERSECTIONS_YTHREADS 1
#define _10_TRIANGLEINTERSECTIONS_ZTHREADS 1

#define SUBDIVS 7



struct BoundingBox
{
    float3 minPoint;
    float3 maxPoint;
};

struct CellTrianglePair
{
    uint cellID;
    uint triangleID;
    uint objectID;
};

struct SortIndices
{
    uint array[4];
};

struct TrianglePair
{
    uint triangleID1;
    uint triangleID2;
    uint objectID1;
    uint objectID2;
};

// berechne aus 3D-Koordinaten, der aktuellen Größe des Grids und dem Level-Offset die 1-dimensionale ID
uint get1DID(uint3 cell3DID, uint resolution, uint offset)
{
    return cell3DID.x + cell3DID.y * resolution + cell3DID.z * resolution * resolution + offset;
}

#define EMPTY 0
#define INTERNAL 1
#define LEAF 2

// wird an zwei Stellen im RadixSort verwendet
// liest 2 Bits an der Bitposition read2BitsFromHere an der Stelle id in cellTrianglePairsSize aus der ZellenID aus und gibt den entsprechenden SortIndice zurück
SortIndices getSortIndicesFromInput(uint id, uint cellTrianglePairsSize, uint read2BitsFromHere, RWStructuredBuffer<CellTrianglePair> cellTrianglePairs)
{
    // sollten keine validen Daten an der Stelle sein, wird ein SortIndies mit 0en gefüllt zurückgegben
    SortIndices resultSortIndices;
    resultSortIndices.array[0] = 0;
    resultSortIndices.array[1] = 0;
    resultSortIndices.array[2] = 0;
    resultSortIndices.array[3] = 0;

    // Sollte die id im validen Bereich liegen (innerhalb des cellTrianglePairsBuffers, der sortIndicesBuffer hat die Größe der nächsten Zweierpotenz der cellTriangleBufferLength)
    if (id < cellTrianglePairsSize)
    {
        CellTrianglePair cellTrianglePair = cellTrianglePairs[id];
        uint bit0 = (cellTrianglePair.cellID >> read2BitsFromHere) & 1; // bitShifte den Input read2BitsFromHere weit und speichere den Wert in bit0
        uint bit1 = (cellTrianglePair.cellID >> read2BitsFromHere + 1) & 1; // bitShifte den Input read2BitsFromHere + 1 weit und speichere den Wert in bit1
        // die beiden bits ergeben zusammen entweder 0, 1, 2 oder 3, an der entprechenden Stelle im reultSortIndices-Array wird eine 1 geschrieben
        if (bit1 == 0 && bit0 == 0) // prüfe, ob die beiden bits zusammen 0 sind
            resultSortIndices.array[3] = 1;
        else if (bit1 == 0 && bit0 == 1) // prüfe, ob die beiden bits zusammen 1 sind
            resultSortIndices.array[2] = 1;
        else if (bit1 == 1 && bit0 == 0) // prüfe, ob die beiden bits zusammen 2 sind
            resultSortIndices.array[1] = 1;
        else if (bit1 == 1 && bit0 == 1) // prüfe, ob die beiden bits zusammen 3 sind
            resultSortIndices.array[0] = 1;
    }
    return resultSortIndices;
}



#endif