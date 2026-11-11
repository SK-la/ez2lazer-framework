// This file is automatically included in every shader.

#ifndef INTERNAL_GLOBAL_UNIFORMS_H
#define INTERNAL_GLOBAL_UNIFORMS_H

layout(std140, set = -1, binding = 0) uniform g_GlobalUniforms
{
    // Whether the backbuffer is currently being drawn to.
    bool g_BackbufferDraw;

    // Whether the depth values range from 0 to 1. If false, depth values range from -1 to 1.
    // OpenGL uses [-1, 1], Vulkan/D3D/MTL all use [0, 1].
    bool g_IsDepthRangeZeroToOne;

    // Whether the clip space ranges from -1 (top) to 1 (bottom). If false, the clip space ranges from -1 (bottom) to 1 (top).
    bool g_IsClipSpaceYInverted;

    // Whether the texture coordinates begin in the top-left of the texture. If false, (0, 0) is the bottom-left texel of the texture.
    bool g_IsUvOriginTopLeft;

    mat4 g_ProjMatrix;
    mat3 g_ToMaskingSpace;

    bool g_IsMasking;
    highp float g_CornerRadius;
    highp float g_CornerExponent;
    highp vec4 g_MaskingRect;
    highp float g_BorderThickness;
    lowp mat4 g_BorderColour;
    mediump float g_MaskingBlendRange;
    lowp float g_AlphaExponent;
    highp vec2 g_EdgeOffset;
    bool g_DiscardInner;
    highp float g_InnerCornerRadius;

    // 0 -> None
    // 1 -> ClampToEdge
    // 2 -> ClampToBorder
    // 3 -> Repeat
    int g_WrapModeS;
    int g_WrapModeT;
};

// Masks for borderless rounded quads. Vertices carry a 1-based index; 0 keeps g_ToMaskingSpace.
// Stored as a flat vec4 array (128 records * 5 vec4). D3D11 can dynamically index a vector array.
// An array of structs cannot: the compiler picks one element for the whole draw, so later panels
// are clipped by the first mask and text is cut in half.
struct VertexMaskRecord
{
    vec4 Column0;
    vec4 Column1;
    vec4 Column2;
    vec4 Rect;
    vec4 Parameters;
};

layout(std140, set = -2, binding = 0) uniform g_VertexMasks
{
    vec4 g_VertexMaskData[640];
};

VertexMaskRecord loadVertexMask(int index)
{
    int baseIndex = index * 5;
    VertexMaskRecord record;
    record.Column0 = g_VertexMaskData[baseIndex];
    record.Column1 = g_VertexMaskData[baseIndex + 1];
    record.Column2 = g_VertexMaskData[baseIndex + 2];
    record.Rect = g_VertexMaskData[baseIndex + 3];
    record.Parameters = g_VertexMaskData[baseIndex + 4];
    return record;
}

// Referenced from every vertex stage so the block survives dead-code elimination.
// Both branches are 0, so the read cannot move a pixel.
float vertexMaskTableAnchor()
{
    return g_VertexMaskData[0].x > 1.0e16 ? 0.0 : 0.0;
}

#endif