// Renders a mesh in UV space with the inputs of Built-in Standard, URP Lit and HDRP Lit materials:
// base color, packed occlusion / roughness / metallic and tangent space normal
Shader "Hidden/ArmorPaint/Bake" {
	SubShader {
		Pass {
			Cull Off ZWrite Off ZTest Always

			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"

			sampler2D _ApBase;
			sampler2D _ApNormal;
			sampler2D _ApMetal;
			sampler2D _ApOcclusion;
			float4 _ApST;
			float4 _ApColor;
			float4 _ApRemap; // metallic min, max, smoothness min, max
			float4 _ApOcclusionRange; // min, max, strength
			float _ApNormalScale;
			float _ApHasNormal;
			float _ApSmoothFromAlbedo;

			struct v2f {
				float4 pos : SV_POSITION;
				float2 uv : TEXCOORD0;
			};

			struct targets {
				float4 base : SV_Target0;
				float4 orm : SV_Target1;
				float4 nor : SV_Target2;
			};

			v2f vert(float2 uv : TEXCOORD0) {
				v2f o;
				o.pos = float4(uv * 2.0 - 1.0, 0.5, 1.0);
				#if UNITY_UV_STARTS_AT_TOP
				o.pos.y = -o.pos.y;
				#endif
				o.uv = uv * _ApST.xy + _ApST.zw;
				return o;
			}

			targets frag(v2f i) {
				float4 albedo = tex2D(_ApBase, i.uv) * _ApColor;
				float4 metal = tex2D(_ApMetal, i.uv);
				float metallic = lerp(_ApRemap.x, _ApRemap.y, metal.r);
				float smoothness = lerp(_ApRemap.z, _ApRemap.w, _ApSmoothFromAlbedo > 0.5 ? albedo.a : metal.a);
				float occlusion = lerp(_ApOcclusionRange.x, _ApOcclusionRange.y, tex2D(_ApOcclusion, i.uv).g);
				occlusion = lerp(1.0, occlusion, _ApOcclusionRange.z);
				float3 n = float3(0.0, 0.0, 1.0);
				if (_ApHasNormal > 0.5) {
					n = UnpackNormal(tex2D(_ApNormal, i.uv));
					n.xy *= _ApNormalScale;
					n = normalize(n);
				}
				targets o;
				o.base = float4(albedo.rgb, 1.0);
				o.orm = float4(occlusion, 1.0 - smoothness, metallic, 1.0);
				o.nor = float4(n * 0.5 + 0.5, 1.0);
				return o;
			}
			ENDCG
		}
	}
}
