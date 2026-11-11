#ifndef TEXTURE2D_VS
#define TEXTURE2D_VS

#include "sh_Utils.h"

layout(location = 0) in highp vec2 m_Position;
layout(location = 1) in lowp vec4 m_Colour;
layout(location = 2) in highp vec2 m_TexCoord;
layout(location = 3) in highp vec4 m_TexRect;
layout(location = 4) in mediump vec2 m_BlendRange;
layout(location = 5) in highp float m_BackbufferDrawDepth;
layout(location = 6) in highp float m_UseVertexMask;
layout(location = 7) in highp vec3 m_MaskColumn0;
layout(location = 8) in highp vec3 m_MaskColumn1;
layout(location = 9) in highp vec3 m_MaskColumn2;
layout(location = 10) in highp vec4 m_MaskingRect;
layout(location = 11) in highp vec4 m_MaskParameters;

layout(location = 0) out highp vec2 v_MaskingPosition;
layout(location = 1) out lowp vec4 v_Colour;
layout(location = 2) out highp vec2 v_TexCoord;
layout(location = 3) out highp vec4 v_TexRect;
layout(location = 4) out mediump vec2 v_BlendRange;
layout(location = 5) out mediump float v_UseVertexMask;
layout(location = 6) out highp vec4 v_MaskingRect;
layout(location = 7) out highp vec4 v_MaskParameters;

void main(void)
{
    // m_UseVertexMask > 0 carries this quad's own mask so consecutive rounded rects can share a batch.
    // Otherwise keep transforming by the uniform matrix (paths, borders, and any vertex that left the flag at 0).
    highp vec3 maskingPos;

    if (m_UseVertexMask > 0.5)
        maskingPos = mat3(m_MaskColumn0, m_MaskColumn1, m_MaskColumn2) * vec3(m_Position, 1.0);
    else
        maskingPos = g_ToMaskingSpace * vec3(m_Position, 1.0);

    v_MaskingPosition = maskingPos.xy / maskingPos.z;

    v_Colour = m_Colour;
    v_TexCoord = m_TexCoord;
    v_TexRect = m_TexRect;
    v_BlendRange = m_BlendRange;
    v_UseVertexMask = m_UseVertexMask;
    v_MaskingRect = m_MaskingRect;
    v_MaskParameters = m_MaskParameters;

    gl_Position = g_ProjMatrix * vec4(m_Position, 1.0, 1.0);

    if (g_BackbufferDraw)
        gl_Position.z = m_BackbufferDrawDepth;
}

#endif