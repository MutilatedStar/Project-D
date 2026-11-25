// Put this file in a folder called "Resources" (Assets/Resources/ScannerGray.shader).
// The Scanner Lens swaps the stage's sprites and tiles to this shader while the lens is on: everything turns black and white,
// and traps get a red Scan Tint instead. It is the normal unlit sprite shader plus a grey-scale step, so it works in the
// built-in pipeline and in URP.
Shader "Hidden/ScannerGray"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _ScanTint ("Scan Tint", Color) = (1,1,1,1)
        _Contrast ("Contrast", Range(0.5, 2)) = 1.15
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
        [PerRendererData] _AlphaTex ("External Alpha", 2D) = "white" {}
        [PerRendererData] _EnableExternalAlpha ("Enable External Alpha", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex SpriteVert
            #pragma fragment ScanFrag
            #pragma target 2.0
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ PIXELSNAP_ON
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"

            fixed4 _ScanTint;
            half _Contrast;

            fixed4 ScanFrag(v2f IN) : SV_Target
            {
                fixed4 c = SpriteFrag(IN);                                   // colour, already multiplied by alpha
                half g = dot(c.rgb, half3(0.299, 0.587, 0.114));             // grey-scale
                g = saturate((g - 0.5 * c.a) * _Contrast + 0.5 * c.a);       // a little contrast around mid grey
                c.rgb = g * _ScanTint.rgb;
                return c;
            }
            ENDCG
        }
    }
}
