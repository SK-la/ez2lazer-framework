// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#ifndef GLYPH_TEXTURE_ARRAY_VS
#define GLYPH_TEXTURE_ARRAY_VS

#include "sh_Utils.h"

layout(location = 0) in highp vec2 m_Position;
layout(location = 1) in lowp vec4 m_Colour;
layout(location = 2) in highp vec2 m_TexCoord;
layout(location = 3) in highp vec4 m_TexRect;
layout(location = 4) in mediump vec2 m_BlendRange;
layout(location = 5) in highp float m_BackbufferDrawDepth;
layout(location = 6) in highp float m_GlyphLayer;
layout(location = 7) in highp float m_UseLegacyTexture;

layout(location = 0) out highp vec2 v_MaskingPosition;
layout(location = 1) out lowp vec4 v_Colour;
layout(location = 2) out highp vec2 v_TexCoord;
layout(location = 3) out highp vec4 v_TexRect;
layout(location = 4) out mediump vec2 v_BlendRange;
layout(location = 5) out mediump float v_UseVertexMask;
layout(location = 6) out highp vec4 v_MaskingRect;
layout(location = 7) out highp vec4 v_MaskParameters;
layout(location = 8) flat out highp float v_GlyphLayer;
layout(location = 9) flat out highp float v_UseLegacyTexture;

void main(void)
{
    highp vec3 maskingPos = g_ToMaskingSpace * vec3(m_Position, 1.0);
    v_MaskingPosition = maskingPos.xy / maskingPos.z;
    v_Colour = m_Colour;
    v_TexCoord = m_TexCoord;
    v_TexRect = m_TexRect;
    v_BlendRange = m_BlendRange;
    v_UseVertexMask = 0.0;
    v_MaskingRect = vec4(0.0);
    v_MaskParameters = vec4(0.0);
    v_GlyphLayer = m_GlyphLayer;
    v_UseLegacyTexture = m_UseLegacyTexture;

    gl_Position = g_ProjMatrix * vec4(m_Position, 1.0, 1.0);

    if (g_BackbufferDraw)
        gl_Position.z = m_BackbufferDrawDepth;
}

#endif
