
//------------------------------------------------------- CONSTANTS

// K = Center of the nearest input pixel.
// O = Center of the output pixel.
//
//          |           |
//    0     |     1     |     2
//          |           |
//          |           |
//  --------+-----------+--------
//          |           |
//          | O         |
//    3     |     K     |     5
//          |           |
//          |           |
//  --------+-----------+--------
//          |           |
//          |           |
//    6     |     7     |     8
//          |           |
//
static const int2 kOffsets3x3[9] =
{
	int2(-1, -1),
	int2( 0, -1),
	int2( 1, -1),
	int2(-1,  0),
	int2( 0,  0), // K
	int2( 1,  0),
	int2(-1,  1),
	int2( 0,  1),
	int2( 1,  1),
};
	
// Indexes of the 3x3 square.
static const uint kSquareIndexes3x3[9] = { 0, 1, 2, 3, 4, 5, 6, 7, 8 };

// Indexes of the offsets to have plus + shape.
static const uint kPlusIndexes3x3[5] = { 1, 3, 4, 5, 7 };

// Number of neighbors.
static const uint kNeighborsCount = 9;

#if AA_UPSAMPLE
	// T = Center of the nearest top left pixel input pixel.
	// O = Center of the output pixel.
	//
	//          | 
	//    T     |     .
	//          | 
	//       O  | 
	//  --------+--------
	//          | 
	//          | 
	//    .     |     .
	//          | 
	static const int2 kOffsets2x2[4] =
	{
		int2( 0,  0), // T
		int2( 1,  0),
		int2( 0,  1),
		int2( 1,  1),
	};
		
	// Indexes of the 2x2 square.
	static const uint kSquareIndexes2x2[4] = { 0, 1, 2, 3 };

#endif // AA_UPSAMPLE

float min3( float a, float b, float c )
{
	return min( a, min( b, c ) );
}

float max3( float a, float b, float c )
{
	return max( a, max( b, c ) );
}

float4 min3( float4 a, float4 b, float4 c )
{
	return float4(
		min3( a.x, b.x, c.x ),
		min3( a.y, b.y, c.y ),
		min3( a.z, b.z, c.z ),
		min3( a.w, b.w, c.w )
	);
}

float4 max3( float4 a, float4 b, float4 c )
{
	return float4(
		max3( a.x, b.x, c.x ),
		max3( a.y, b.y, c.y ),
		max3( a.z, b.z, c.z ),
		max3( a.w, b.w, c.w )
	);
}

float3 RGBToYCoCg( float3 RGB )
{
	float Y  = dot( RGB, float3(  1, 2,  1 ) );
	float Co = dot( RGB, float3(  2, 0, -2 ) );
	float Cg = dot( RGB, float3( -1, 2, -1 ) );
	
	float3 YCoCg = float3( Y, Co, Cg );
	return YCoCg;
}

float3 YCoCgToRGB( float3 YCoCg )
{
	float Y  = YCoCg.x * 0.25;
	float Co = YCoCg.y * 0.25;
	float Cg = YCoCg.z * 0.25;

	float R = Y + Co - Cg;
	float G = Y + Cg;
	float B = Y - Co - Cg;

	float3 RGB = float3( R, G, B );
	return RGB;
}

float HistoryClip(float3 history, float3 filtered, float3 neighborMin, float3 neighborMax)
{
	float3 boxMin = neighborMin;
	float3 boxMax = neighborMax;
	//float3 boxMin = min( filtered, neighborMin );
	//float3 boxMax = max( filtered, neighborMax );

	float3 origin = history;
	float3 direction = filtered - history;
	direction = abs( direction ) < (1.0/65536.0) ? (1.0/65536.0) : direction;
	float3 invDirection = rcp( direction );

	float3 minIntersect = (boxMin - origin) * invDirection;
	float3 maxIntersect = (boxMax - origin) * invDirection;
	float3 enterIntersect = min( minIntersect, maxIntersect );
	return max3( enterIntersect.x, enterIntersect.y, enterIntersect.z );
}

// -------------------------------------------------------------------------------------------------
// Bicubic2DCatmullRom

void Bicubic2DCatmullRom( in float2 UV, in float2 Size, in float2 InvSize, out float2 Sample[3], out float2 Weight[3] )
{
	UV *= Size;

	float2 tc = floor( UV - 0.5 ) + 0.5;
	float2 f = UV - tc;
	float2 f2 = f * f;
	float2 f3 = f2 * f;

	float2 w0 = f2 - 0.5 * (f3 + f);
	float2 w1 = 1.5 * f3 - 2.5 * f2 + 1;
	float2 w3 = 0.5 * (f3 - f2);
	float2 w2 = 1 - w0 - w1 - w3;

	Weight[0] = w0;
	Weight[1] = w1 + w2;
	Weight[2] = w3;

	Sample[0] = tc - 1;
	Sample[1] = tc + w2 / Weight[1];
	Sample[2] = tc + 2;
 
	Sample[0] *= InvSize;
	Sample[1] *= InvSize;
	Sample[2] *= InvSize;
}

#define BICUBIC_CATMULL_ROM_SAMPLES 5

struct FCatmullRomSamples
{
	// Constant number of samples (BICUBIC_CATMULL_ROM_SAMPLES)
	uint Count;
	
	// Constant sign of the UV direction from master UV sampling location.
	int2 UVDir[BICUBIC_CATMULL_ROM_SAMPLES];

	// Bilinear sampling UV coordinates of the samples
	float2 UV[BICUBIC_CATMULL_ROM_SAMPLES];

	// Weights of the samples
	float Weight[BICUBIC_CATMULL_ROM_SAMPLES];

	// Final multiplier (it is faster to multiply 3 RGB values than reweights the 5 weights)
	float FinalMultiplier;
};

FCatmullRomSamples GetBicubic2DCatmullRomSamples(float2 UV, float2 Size, in float2 InvSize)
{
	FCatmullRomSamples Samples;
	Samples.Count = BICUBIC_CATMULL_ROM_SAMPLES;
	
	float2 Weight[3];
	float2 Sample[3];
	Bicubic2DCatmullRom( UV, Size, InvSize, Sample, Weight );

	// Optimized by removing corner samples
	Samples.UV[0] = float2(Sample[1].x, Sample[0].y);
	Samples.UV[1] = float2(Sample[0].x, Sample[1].y);
	Samples.UV[2] = float2(Sample[1].x, Sample[1].y);
	Samples.UV[3] = float2(Sample[2].x, Sample[1].y);
	Samples.UV[4] = float2(Sample[1].x, Sample[2].y);
	
	Samples.Weight[0] = Weight[1].x * Weight[0].y;
	Samples.Weight[1] = Weight[0].x * Weight[1].y;
	Samples.Weight[2] = Weight[1].x * Weight[1].y;
	Samples.Weight[3] = Weight[2].x * Weight[1].y;
	Samples.Weight[4] = Weight[1].x * Weight[2].y;

	Samples.UVDir[0] = int2(0, -1);
	Samples.UVDir[1] = int2(-1, 0);
	Samples.UVDir[2] = int2(0, 0);
	Samples.UVDir[3] = int2(1, 0);
	Samples.UVDir[4] = int2(0, 1);

	// Reweight after removing the corners
	float CornerWeights;
	CornerWeights  = Samples.Weight[0];
	CornerWeights += Samples.Weight[1];
	CornerWeights += Samples.Weight[2];
	CornerWeights += Samples.Weight[3];
	CornerWeights += Samples.Weight[4];
	Samples.FinalMultiplier = 1 / CornerWeights;

	return Samples;
}