#ifndef TEXTURE2D_VS
#define TEXTURE2D_VS

#include "sh_Utils.h"

layout(location = 0) in highp vec2 m_Position;
layout(location = 1) in lowp vec4 m_Colour;
layout(location = 2) in highp vec2 m_TexCoord;
layout(location = 3) in highp vec4 m_TexRect;
layout(location = 4) in mediump vec2 m_BlendRange;
layout(location = 5) in highp float m_BackbufferDrawDepth;
layout(location = 6) in highp float m_MaskIndex;

layout(location = 0) out highp vec2 v_MaskingPosition;
layout(location = 1) out lowp vec4 v_Colour;
layout(location = 2) out highp vec2 v_TexCoord;
layout(location = 3) out highp vec4 v_TexRect;
layout(location = 4) out mediump vec2 v_BlendRange;
layout(location = 5) out mediump float v_MaskIndex;

void main(void)
{
    // m_MaskIndex > 0 selects this quad's mask from g_VertexMaskRecords so consecutive rounded rects share a batch.
    // 0 keeps the uniform matrix (paths, borders, and any vertex that left the index at 0).
    highp vec3 maskingPos;

    if (m_MaskIndex > 0.5)
    {
        VertexMaskRecord record = g_VertexMaskRecords[int(m_MaskIndex + 0.5) - 1];
        maskingPos = mat3(record.Column0.xyz, record.Column1.xyz, record.Column2.xyz) * vec3(m_Position, 1.0);
    }
    else
        maskingPos = g_ToMaskingSpace * vec3(m_Position, 1.0);

    v_MaskingPosition = maskingPos.xy / maskingPos.z;

    v_Colour = m_Colour;
    v_TexCoord = m_TexCoord;
    v_TexRect = m_TexRect;
    v_BlendRange = m_BlendRange;
    v_MaskIndex = m_MaskIndex;

    gl_Position = g_ProjMatrix * vec4(m_Position, 1.0, 1.0);

    if (g_BackbufferDraw)
        gl_Position.z = m_BackbufferDrawDepth;
}

#endif