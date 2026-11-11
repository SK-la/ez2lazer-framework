#ifndef MASKING_H
#define MASKING_H

layout(location = 0) in highp vec2 v_MaskingPosition;
layout(location = 1) in lowp vec4 v_Colour;

#ifdef HIGH_PRECISION_VERTEX
	layout(location = 3) in highp vec4 v_TexRect;
#else
	layout(location = 3) in mediump vec4 v_TexRect;
#endif

layout(location = 4) in mediump vec2 v_BlendRange;
layout(location = 5) in mediump float v_MaskIndex;

bool useVertexMask()
{
	return v_MaskIndex > 0.5;
}

VertexMaskRecord activeVertexMask()
{
	return g_VertexMaskRecords[int(v_MaskIndex + 0.5) - 1];
}

highp vec4 activeMaskingRect()
{
	return useVertexMask() ? activeVertexMask().Rect : g_MaskingRect;
}

highp float activeCornerRadius()
{
	return useVertexMask() ? activeVertexMask().Parameters.x : g_CornerRadius;
}

highp float activeCornerExponent()
{
	return useVertexMask() ? activeVertexMask().Parameters.y : g_CornerExponent;
}

mediump float activeMaskingBlendRange()
{
	return useVertexMask() ? activeVertexMask().Parameters.z : g_MaskingBlendRange;
}

lowp float activeAlphaExponent()
{
	return useVertexMask() ? activeVertexMask().Parameters.w : g_AlphaExponent;
}

highp float distanceFromRoundedRect(highp vec2 offset, highp float radius, highp vec4 rect, highp float exponent)
{
	highp vec2 maskingPosition = v_MaskingPosition + offset;

	// Compute offset distance from masking rect in masking space.
	highp vec2 topLeftOffset = rect.xy - maskingPosition;
	highp vec2 bottomRightOffset = maskingPosition - rect.zw;

	highp vec2 distanceFromShrunkRect = max(
		bottomRightOffset + vec2(radius),
		topLeftOffset + vec2(radius));

	highp float maxDist = max(distanceFromShrunkRect.x, distanceFromShrunkRect.y);

	// Inside the shrunk rectangle
	if (maxDist <= 0.0)
		return maxDist;
	// Outside of the shrunk rectangle
	else
	{
		distanceFromShrunkRect = max(vec2(0.0), distanceFromShrunkRect);
		return pow(pow(distanceFromShrunkRect.x, exponent) + pow(distanceFromShrunkRect.y, exponent), 1.0 / exponent);
	}
}

highp float distanceFromDrawingRect(mediump vec2 texCoord)
{
	highp vec2 topLeftOffset = v_TexRect.xy - texCoord;
	topLeftOffset = vec2(
		v_BlendRange.x > 0.0 ? topLeftOffset.x / v_BlendRange.x : 0.0,
		v_BlendRange.y > 0.0 ? topLeftOffset.y / v_BlendRange.y : 0.0);

	highp vec2 bottomRightOffset = texCoord - v_TexRect.zw;
	bottomRightOffset = vec2(
		v_BlendRange.x > 0.0 ? bottomRightOffset.x / v_BlendRange.x : 0.0,
		v_BlendRange.y > 0.0 ? bottomRightOffset.y / v_BlendRange.y : 0.0);

	highp vec2 xyDistance = max(topLeftOffset, bottomRightOffset);
	return max(xyDistance.x, xyDistance.y);
}

lowp vec4 getBorderColour()
{
    highp vec4 rect = activeMaskingRect();
    highp vec2 relativeTexCoord = v_MaskingPosition / (rect.zw - rect.xy);
    lowp vec4 top = mix(g_BorderColour[0], g_BorderColour[2], relativeTexCoord.x);
    lowp vec4 bottom = mix(g_BorderColour[1], g_BorderColour[3], relativeTexCoord.x);
    return mix(top, bottom, relativeTexCoord.y);
}

lowp vec4 getRoundedColor(lowp vec4 texel, mediump vec2 texCoord)
{
	// Keeps g_VertexMasks in fragment shaders that mask. The anchor is 0 for every rect this table stores.
	texel.a += vertexMaskTableAnchor();

	// Vertex masks are only written for non-hollow, borderless quads. Hollow / border draws keep the uniform path.
	bool vertexMask = useVertexMask();

	if (!vertexMask && !g_IsMasking && v_BlendRange == vec2(0.0))
	{
		return v_Colour * texel;
	}

	highp vec4 rect = activeMaskingRect();
	highp float radius = activeCornerRadius();
	highp float exponent = activeCornerExponent();
	mediump float blendRange = activeMaskingBlendRange();
	lowp float alphaExponent = activeAlphaExponent();

	highp float dist = distanceFromRoundedRect(vec2(0.0), radius, rect, exponent);

	// Inside the corner inset the fade has not started. Panel fills take this path and skip the rest of the SDF.
	if (vertexMask && dist <= 0.0 && v_BlendRange == vec2(0.0))
		return v_Colour * texel;

	lowp float alphaFactor = 1.0;

	// Discard inner pixels. Never taken for vertex-masked quads (those are not hollow).
	if (!vertexMask && g_DiscardInner)
	{
		highp float innerDist = (g_EdgeOffset == vec2(0.0) && g_InnerCornerRadius == g_CornerRadius) ?
			dist : distanceFromRoundedRect(g_EdgeOffset, g_InnerCornerRadius, g_MaskingRect, g_CornerExponent);

		// v_BlendRange is set from outside in a hacky way to tell us the g_MaskingBlendRange used for the rounded
		// corners of the edge effect container itself. We can then derive the alpha factor for smooth inner edge
		// effect from that.
		highp float innerBlendFactor = (g_InnerCornerRadius - g_MaskingBlendRange - innerDist) / v_BlendRange.x;
		if (innerBlendFactor > 1.0)
		{
			return vec4(0.0);
		}

		// We exponentiate our factor to exactly counteract the later exponentiation by g_AlphaExponent for a smoother inner border.
		alphaFactor = pow(min(1.0 - innerBlendFactor, 1.0), 1.0 / alphaExponent);
	}

	dist /= blendRange;

	// This correction is needed to avoid fading of the alpha value for radii below 1px.
	highp float radiusCorrection = radius <= 0.0 ? blendRange : max(0.0, blendRange - radius);
	highp float fadeStart = (radius + radiusCorrection) / blendRange;
	alphaFactor *= min(fadeStart - dist, 1.0);

	if (v_BlendRange.x > 0.0 || v_BlendRange.y > 0.0)
	{
		alphaFactor *= clamp(1.0 - distanceFromDrawingRect(texCoord), 0.0, 1.0);
	}

	if (alphaFactor <= 0.0)
	{
		return vec4(0.0);
	}

	// This ends up softening glow without negatively affecting edge smoothness much.
	alphaFactor = pow(alphaFactor, alphaExponent);

	lowp vec4 contentColour = v_Colour * texel;

	if (vertexMask)
		return vec4(contentColour.rgb, contentColour.a * alphaFactor);

	highp float borderStart = 1.0 + fadeStart - g_BorderThickness;
	lowp float colourWeight = min(borderStart - dist, 1.0);

	if (colourWeight == 1.0)
		return vec4(contentColour.rgb, contentColour.a * alphaFactor);

	lowp vec4 borderColour = getBorderColour();

	if (colourWeight <= 0.0)
		return vec4(borderColour.rgb, borderColour.a * alphaFactor);

	contentColour.a *= alphaFactor;
	borderColour.a *= 1.0 - colourWeight;
	return blend(borderColour, contentColour);
}

#endif