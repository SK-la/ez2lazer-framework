// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#ifndef GLYPH_TEXTURE_ARRAY_FS
#define GLYPH_TEXTURE_ARRAY_FS

#include "sh_Utils.h"
#include "sh_Masking.h"
#include "sh_TextureWrapping.h"

layout(location = 2) in mediump vec2 v_TexCoord;
layout(location = 8) flat in highp float v_GlyphLayer;
layout(location = 9) flat in highp float v_UseLegacyTexture;

layout(set = 0, binding = 0) uniform lowp texture2DArray m_GlyphAtlas;
layout(set = 0, binding = 1) uniform lowp sampler m_GlyphSampler;
layout(set = 1, binding = 0) uniform lowp texture2D m_LegacyTexture;
layout(set = 1, binding = 1) uniform lowp sampler m_LegacySampler;

layout(location = 0) out vec4 o_Colour;

void main(void)
{
    vec2 wrappedCoord = wrap(v_TexCoord, v_TexRect);

    if ((g_WrapModeS == 2 && (wrappedCoord.x < v_TexRect[0] || wrappedCoord.x > v_TexRect[2]))
        || (g_WrapModeT == 2 && (wrappedCoord.y < v_TexRect[1] || wrappedCoord.y > v_TexRect[3])))
    {
        o_Colour = vec4(0.0);
    }
    else
    {
        lowp vec4 texel;

        if (v_UseLegacyTexture > 0.5)
            texel = wrappedSampler(wrappedCoord, v_TexRect, m_LegacyTexture, m_LegacySampler, -0.9);
        else
        {
            float coverage = texture(sampler2DArray(m_GlyphAtlas, m_GlyphSampler), vec3(wrappedCoord, v_GlyphLayer), -0.9).r;
            texel = vec4(1.0, 1.0, 1.0, coverage);
        }

        o_Colour = getRoundedColor(texel, wrappedCoord);
    }
}

#endif
