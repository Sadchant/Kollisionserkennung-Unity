#ifndef COMPUTE_SHADER_GLOBALS
#define COMPUTE_SHADER_GLOBALS



#define _1_BOUNDINGBOXES_XTHREADS 1024
#define _1_BOUNDINGBOXES_YTHREADS 1
#define _1_BOUNDINGBOXES_ZTHREADS 1

#define _2_SCENEBOUNDINGBOX_XTHREADS 1024
#define _2_SCENEBOUNDINGBOX_YTHREADS 1
#define _2_SCENEBOUNDINGBOX_ZTHREADS 1

#define _3_FILLCOUNTERTREES_XTHREADS 1024
#define _3_FILLCOUNTERTREES_YTHREADS 1
#define _3_FILLCOUNTERTREES_ZTHREADS 1

#define _4_GLOBALCOUNTERTREE_XTHREADS 1024
#define _4_GLOBALCOUNTERTREE_YTHREADS 1
#define _4_GLOBALCOUNTERTREE_ZTHREADS 1

#define _5_FILLTYPETREE_XTHREADS 8
#define _5_FILLTYPETREE_YTHREADS 8
#define _5_FILLTYPETREE_ZTHREADS 8

#define SUBDIVS 7



struct BoundingBox
{
    float3 minPoint;
    float3 maxPoint;
};

// berechne aus 3D-Koordinaten, der aktuellen Größe des Grids und dem Level-Offset die 1-dimensionale ID
uint get1DID(uint x, uint y, uint z, uint resolution, uint offset)
{
    return x + y * resolution + z * resolution * resolution + offset;
}

#define EMPTY 0
#define INTERNAL 1
#define LEAF 2



#endif