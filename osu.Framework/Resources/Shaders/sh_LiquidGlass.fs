#ifndef LIQUID_GLASS_FS
#define LIQUID_GLASS_FS

#undef HIGH_PRECISION_VERTEX
#define HIGH_PRECISION_VERTEX

#include "sh_Utils.h"
#include "sh_Masking.h"

layout(location = 2) in highp vec2 v_TexCoord;

layout(std140, set = 0, binding = 0) uniform m_LiquidGlassParameters
{
	// 卡片在采样纹理里的归一化子矩形，xy 为左上角，zw 为尺寸。
	highp vec4 lg_CardUvRect;

	// 一个屏幕像素对应多少归一化 UV。折射偏移按屏幕像素给出，再换算到纹理空间。
	highp vec2 lg_UvPerPixel;

	highp vec2 lg_CardSize;
	highp vec2 lg_LightDirection;
	lowp vec4 lg_Tint;
	highp float lg_CornerRadius;
	highp float lg_RefractionDepth;
	highp float lg_RefractionStrength;
	highp float lg_ChromaticStrength;
	highp float lg_EdgeHighlight;
	highp float lg_InnerShadow;
	highp float lg_TintStrength;
};

layout(set = 1, binding = 0) uniform lowp texture2D m_Texture;
layout(set = 1, binding = 1) uniform lowp sampler m_Sampler;

layout(location = 0) out vec4 o_Colour;

// 负值表示在形状内部。用于求边缘距离梯度（假法线）。
highp float sdRoundedRect(highp vec2 p, highp vec2 halfSize, highp float radius)
{
	highp float r = min(radius, min(halfSize.x, halfSize.y));
	highp vec2 q = abs(p) - halfSize + vec2(r);
	return length(max(q, vec2(0.0))) + min(max(q.x, q.y), 0.0) - r;
}

lowp vec4 sampleGlass(highp vec2 coord, highp vec2 minCoord, highp vec2 maxCoord)
{
	return texture(sampler2D(m_Texture, m_Sampler), clamp(coord, minCoord, maxCoord));
}

void main(void)
{
	highp vec2 uvSize = max(lg_CardUvRect.zw, vec2(0.000001));
	highp vec2 local = (v_TexCoord - lg_CardUvRect.xy) / uvSize;

	highp vec2 halfSize = lg_CardSize * 0.5;
	highp vec2 centered = local * lg_CardSize - halfSize;

	// 正值表示在圆角矩形内部的距离。
	highp float inside = -sdRoundedRect(centered, halfSize, lg_CornerRadius);

	// 有限差分求梯度；梯度指向外侧。
	highp float epsilon = max(min(lg_CardSize.x, lg_CardSize.y) * 0.002, 0.5);
	highp vec2 gradient = vec2(
		sdRoundedRect(centered + vec2(epsilon, 0.0), halfSize, lg_CornerRadius) - sdRoundedRect(centered - vec2(epsilon, 0.0), halfSize, lg_CornerRadius),
		sdRoundedRect(centered + vec2(0.0, epsilon), halfSize, lg_CornerRadius) - sdRoundedRect(centered - vec2(0.0, epsilon), halfSize, lg_CornerRadius));

	highp vec2 outward = length(gradient) > 0.000001 ? normalize(gradient) : vec2(0.0);

	// 0 = 边界，1 = 折射带内侧。
	highp float band = max(lg_RefractionDepth, 0.001);
	highp float edge = clamp(inside / band, 0.0, 1.0);
	highp float lens = (1.0 - edge) * (1.0 - edge);

	highp vec2 offset = outward * (lens * lg_RefractionStrength) * lg_UvPerPixel;
	highp float chroma = lens * lg_ChromaticStrength;

	// 只允许在采样纹理的有效范围内取像素，避免外扩捕获区之外变成钳制涂抹。
	highp vec2 minCoord = vec2(0.0);
	highp vec2 maxCoord = vec2(1.0);

	lowp vec3 colour = vec3(0.0);
	colour.r = sampleGlass(v_TexCoord + offset * (1.0 + chroma), minCoord, maxCoord).r;
	colour.g = sampleGlass(v_TexCoord + offset, minCoord, maxCoord).g;
	colour.b = sampleGlass(v_TexCoord + offset * (1.0 - chroma), minCoord, maxCoord).b;

	highp float rim = 1.0 - edge;
	highp vec2 light = normalize(lg_LightDirection + vec2(0.000001));
	highp float facing = max(dot(outward, light), 0.0);
	highp float awayFromLight = max(dot(-outward, light), 0.0);

	colour += vec3(rim * rim * rim * lg_EdgeHighlight * (0.25 + 0.75 * facing));
	colour *= 1.0 - rim * rim * lg_InnerShadow * awayFromLight;

	colour = mix(colour, lg_Tint.rgb, clamp(lg_Tint.a * lg_TintStrength, 0.0, 1.0));

	o_Colour = getRoundedColor(vec4(colour, 1.0), v_TexCoord);
}

#endif
