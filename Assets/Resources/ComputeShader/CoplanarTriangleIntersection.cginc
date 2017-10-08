#ifndef COPLANAR_TRIANGLE_INTERSECTIONS
// Upgrade NOTE: excluded shader from OpenGL ES 2.0 because it uses non-square matrices
#pragma exclude_renderers gles
#define COPLANAR_TRIANGLE_INTERSECTIONS


// ***** Quelle: http://blackpawn.com/texts/pointinpoly/default.html
bool SameSide(float3 p1, float3 p2, float3 a, float3 b)
{
    float3 cp1 = cross(b - a, p1 - a);
    float3 cp2 = cross(b - a, p2 - a);
    float dotProduct = dot(cp1, cp2);
    return (dotProduct >= 0);
}

// *****


#define OFFSET 0.01f

bool IntersectCoplanar(float3 triangle1[3], float3 triangle2[3])
{
    // aus zwei Kanten von triangle1 die Normale normal1 berechnen
    float3 edge1 = normalize(triangle1[1] - triangle1[0]);
    float3 edge2 = normalize(triangle1[2] - triangle1[0]);
    float3 normal1 = normalize(cross(edge1, edge2));
    // das selbe für das zweite Dreieck
    edge1 = normalize(triangle2[1] - triangle2[0]);
    edge2 = normalize(triangle2[2] - triangle2[0]);
    float3 normal2 = normalize(cross(edge1, edge2));
    // wenn das SkalarProdukt der beiden Normalen -1 oder 1 ist, sind die Normalen parallel
    float absNormalsDotProduct = abs(dot(normal1, normal2));
    if (!((absNormalsDotProduct > (1 - OFFSET)) && (absNormalsDotProduct < (1 + OFFSET)))) // offset, um keine floats auf Gleichheit zu überprüfen
        return false; // sollten die Normalen unterschiedlich sein, schneiden sich die Dreiecke nicht coplanar

    // Berechne mit der Hesseschen Normalform den Abstand der Dreiecks-Ebenen zum Ursprung
    float distance1 = dot(normal1, triangle1[0]);
    float distance2 = dot(normal1, triangle2[0]);
    if (!((distance1 > (distance2 - OFFSET)) && (distance1 < (distance2 + OFFSET))))
        return false; // sollte die Distanz unterschiedlich sein können sich die Dreiecke auch nicht coplanar schneiden

    // Berechne eine Projektionsmatrix, die die 3D-Dreiecke in ihre gemeinsme 2D-Ebene projeziert
    float3 secondRow = cross(normal1, edge1);
    float2x3 project2DMatrix =
    {
        edge1.x, edge1.y, edge1.z,
                                 secondRow.x, secondRow.y, secondRow.z
    };
    // projeziere die 3D-Dreiecke in den 2D-Raum
    float2 _2DTriangle1[3];
    float2 _2DTriangle2[3];
    float3 _3D0Triangle1[3];
    float3 _3D0Triangle2[3];
    [unroll]
    for (int i = 0; i < 3; i++)
    {
        _2DTriangle1[i] = mul(project2DMatrix, triangle1[i]);
        _2DTriangle2[i] = mul(project2DMatrix, triangle2[i]);
        // erzeuge die benötigten 3D-Dreiecke aus den 2D-Dreiecken + letzte Stelle = 0
        _3D0Triangle1[i] = float3(_2DTriangle1[i].x, _2DTriangle1[i].y, 0);
        _3D0Triangle2[i] = float3(_2DTriangle2[i].x, _2DTriangle2[i].y, 0);
    }

    for (int j = 0; j < 3; j++)
    {
        float3 t1p1 = _3D0Triangle1[j], t1p2 = _3D0Triangle1[(j + 1) % 3], thirdPoint = _3D0Triangle1[(j + 2) % 3];
        if (!SameSide(thirdPoint, _3D0Triangle2[0], t1p1, t1p2) && !SameSide(thirdPoint, _3D0Triangle2[1], t1p1, t1p2) && !SameSide(thirdPoint, _3D0Triangle2[2], t1p1, t1p2))
            return false;
    }
    return true;
}


#endif