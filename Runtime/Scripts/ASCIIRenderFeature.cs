using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System.ComponentModel;

namespace OccaSoftware
{
    public class ASCIIRenderFeature : ScriptableRendererFeature
    {
        class AsciiPass : ScriptableRenderPass
        {
            private Material asciiMaterial;

            private RTHandle asciiRenderTarget;
            private RTHandle tempHandle;
            private RTHandle source;

            //For Rescaling
            private Material rescaleMaterial;
            private const int MaxRescaleSize = 8;

            RTHandle[] rescaleHandles = new RTHandle[MaxRescaleSize];
            RTHandle finalUpscaleHandle;

            private int iterations = 5;
            ASCIIShaderData shaderData;

            const string targetId = "_ASCIITarget";
            const string finalId = "_FinalUpscale";
            readonly ProfilingSampler sampler = new ProfilingSampler("Ascii");

            public AsciiPass(int iterations)
            {
                this.iterations = iterations;

                asciiRenderTarget = RTHandles.Alloc(Shader.PropertyToID(targetId), targetId);
                finalUpscaleHandle = RTHandles.Alloc(Shader.PropertyToID(finalId), finalId);

                for (int i = 0; i < MaxRescaleSize; i++)
                {
                    rescaleHandles[i] = RTHandles.Alloc(Shader.PropertyToID("_Sample_" + i), "_Sample_" + i);
                }
            }

            public void SetTarget(RTHandle colorHandle)
            {
                source = colorHandle;
            }

            public void SetShaderData(ASCIIShaderData shaderData)
            {
                this.shaderData = shaderData;
            }

            public void SetAsciiMaterialParameters()
            {
                if (asciiMaterial == null)
                    return;

                asciiMaterial.SetFloat(ShaderParams.numberOfCharacters, shaderData.numberOfCharacters);
                asciiMaterial.SetVector(ShaderParams.resolution, shaderData.resolution);
                asciiMaterial.SetFloat(ShaderParams.fontRatio, shaderData.fontRatio);
                asciiMaterial.SetColor(ShaderParams.fontColor, shaderData.fontColor);
                asciiMaterial.SetFloat(ShaderParams.fontColorStrength, shaderData.fontColorStrength);
                asciiMaterial.SetColor(ShaderParams.backingColor, shaderData.backingColor);
                asciiMaterial.SetFloat(ShaderParams.backingColorStrength, shaderData.backingColorStrength);
                asciiMaterial.SetTexture(ShaderParams.fontAsset, shaderData.fontAsset);
            }

            // This method is called before executing the render pass.
            // It can be used to configure render targets and their clear state. Also to create temporary render target textures.
            // When empty this render pass will render to the active camera render target.
            // You should never call CommandBuffer.SetRenderTarget. Instead call <c>ConfigureTarget</c> and <c>ConfigureClear</c>.
            // The render pipeline will ensure target setup and clearing happens in an performance manner.
            public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
            {
                ConfigureTarget(source);
                RenderTextureDescriptor rtDescriptor = cameraTextureDescriptor;
                rtDescriptor.depthBufferBits = 0;
                rtDescriptor.colorFormat = RenderTextureFormat.DefaultHDR;

                RenderingUtils.ReAllocateIfNeeded(
                    ref asciiRenderTarget,
                    rtDescriptor,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: asciiRenderTarget.name
                );

                RenderingUtils.ReAllocateIfNeeded(
                    ref finalUpscaleHandle,
                    rtDescriptor,
                    FilterMode.Point,
                    TextureWrapMode.Clamp,
                    name: finalUpscaleHandle.name
                );

                CoreUtils.Destroy(asciiMaterial);
                CoreUtils.Destroy(rescaleMaterial);

                asciiMaterial = CoreUtils.CreateEngineMaterial("Shader Graphs/ASCII Shader");

                if (iterations >= 1)
                    rescaleMaterial = CoreUtils.CreateEngineMaterial("Shader Graphs/Image Filtering Shader");
            }

            // Here you can implement the rendering logic.
            // Use <c>ScriptableRenderContext</c> to issue drawing commands or execute command buffers
            // https://docs.unity3d.com/ScriptReference/Rendering.ScriptableRenderContext.html
            // You don't have to call ScriptableRenderContext.submit, the render pipeline will call it at specific points in the pipeline.
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                CommandBuffer cmd = CommandBufferPool.Get("ASCIIPass");

                if (asciiMaterial == null)
                    return;
                using (new ProfilingScope(cmd, sampler))
                {
                    RenderTextureDescriptor rtDescriptor = renderingData.cameraData.cameraTargetDescriptor;
                    rtDescriptor.depthBufferBits = 0;
                    rtDescriptor.colorFormat = RenderTextureFormat.DefaultHDR;

                    SetAsciiMaterialParameters();

                    if (iterations >= 1)
                    {
                        if (rescaleMaterial == null)
                            return;

                        RenderTextureDescriptor downscaleDescriptor = rtDescriptor;
                        rescaleMaterial.SetFloat("_Rescale_UVOffset", 1.0f);

                        for (int i = 0; i < iterations; i++)
                        {
                            downscaleDescriptor.width = Mathf.Max(1, downscaleDescriptor.width >> 1);
                            downscaleDescriptor.height = Mathf.Max(1, downscaleDescriptor.height >> 1);

                            RenderingUtils.ReAllocateIfNeeded(
                                ref rescaleHandles[i],
                                downscaleDescriptor,
                                FilterMode.Bilinear,
                                TextureWrapMode.Clamp,
                                name: rescaleHandles[i].name
                            );

                            RTHandle downscaleSource = i == 0 ? source : rescaleHandles[i - 1];
                            Blitter.BlitCameraTexture(cmd, downscaleSource, rescaleHandles[i], rescaleMaterial, 0);
                        }

                        rescaleMaterial.SetFloat("_Rescale_UVOffset", 0.5f);
                        for (int i = 1; i < iterations; i++)
                        {
                            RTHandle currentSource = rescaleHandles[iterations - i];
                            RTHandle currentTarget = rescaleHandles[iterations - i - 1];
                            Blitter.BlitCameraTexture(cmd, currentSource, currentTarget, rescaleMaterial, 0);
                        }

                        RenderingUtils.ReAllocateIfNeeded(
                            ref finalUpscaleHandle,
                            rtDescriptor,
                            FilterMode.Point,
                            TextureWrapMode.Clamp,
                            name: finalId
                        );

                        Blitter.BlitCameraTexture(cmd, rescaleHandles[0], finalUpscaleHandle, rescaleMaterial, 0);
                    }

                    RTHandle asciiMatInputTex = iterations >= 1 ? finalUpscaleHandle : source;
                    Blitter.BlitCameraTexture(cmd, asciiMatInputTex, asciiRenderTarget, asciiMaterial, 0);
                    Blitter.BlitCameraTexture(cmd, asciiRenderTarget, source);
                }

                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
                CommandBufferPool.Release(cmd);
            }

            void Dispose()
            {
                asciiRenderTarget?.Release();
                finalUpscaleHandle?.Release();
                foreach (RTHandle handle in rescaleHandles)
                {
                    handle?.Release();
                }
            }

            /// Cleanup any allocated resources that were created during the execution of this render pass.
            public override void FrameCleanup(CommandBuffer cmd) { }
        }

        [System.Serializable]
        public class Settings
        {
            [Header("Font Settings")]
            public Texture2D fontAsset;

            [Min(1)]
            public int numberOfCharacters = 10;

            [Min(1)]
            public float fontRatio = 3;

            [Header("Display Settings")]
            [Min(1)]
            public int columnCount = 128;

            public AspectRatio aspectRatioDesc = AspectRatio.OneToOne;

            [HideInInspector]
            public Vector2Int aspectRatio = new Vector2Int(1, 1);
            public bool flipAspect = false;

            [ColorUsage(false, true)]
            public Color fontColor = Color.white;

            [Range(0f, 1f)]
            public float fontColorStrength = 0.0f;

            [ColorUsage(false, true)]
            public Color backingColor = Color.black;

            [Range(0f, 1f)]
            public float backingColorStrength = 0.8f;

            [Header("Rescaling Settings")]
            [Range(0, 8)]
            public int iterations = 2;
        }

        AsciiPass asciiPass;
        public Settings settings = new Settings();
        ASCIIShaderData shaderData;

        [HideInInspector]
        public Vector2Int cachedScreenResolution;

        [HideInInspector]
        public int cachedColumnCount;

        Vector2Int screenSize;

        public override void Create()
        {
            asciiPass = new AsciiPass(settings.iterations);
            screenSize = new Vector2Int(Screen.width, Screen.height);
            cachedColumnCount = settings.columnCount;
            UpdateResolutionParam();

            // Configures where the render pass should be injected.
            asciiPass.renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        }

        private void UpdateResolutionParam()
        {
            settings.aspectRatio = GetAspectRatio(settings.aspectRatioDesc, settings.aspectRatio, settings.flipAspect);
            cachedScreenResolution = new Vector2Int(settings.columnCount, 1);
            cachedScreenResolution.y = (int)(
                ((float)Screen.height / Screen.width) * cachedScreenResolution.x * ((float)settings.aspectRatio.x / settings.aspectRatio.y)
            );
        }

        // Here you can inject one or multiple render passes in the renderer.
        // This method is called when setting up the renderer once per-camera.
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(asciiPass);
        }

        public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
        {
            if (screenSize.x != Screen.width || screenSize.y != Screen.height || cachedColumnCount != settings.columnCount)
            {
                cachedColumnCount = settings.columnCount;
                screenSize = new Vector2Int(Screen.width, Screen.height);
                UpdateResolutionParam();
            }

            shaderData = new ASCIIShaderData(
                settings.numberOfCharacters,
                cachedScreenResolution,
                settings.fontRatio,
                settings.fontColor,
                settings.fontColorStrength,
                settings.backingColor,
                settings.backingColorStrength,
                settings.fontAsset
            );

            asciiPass.SetShaderData(shaderData);

            asciiPass.ConfigureInput(ScriptableRenderPassInput.Color);
            asciiPass.SetTarget(renderer.cameraColorTargetHandle);
        }

        public class ASCIIShaderData
        {
            public int numberOfCharacters;
            public Vector4 resolution;
            public float fontRatio;
            public Color fontColor;
            public float fontColorStrength;
            public Color backingColor;
            public float backingColorStrength;
            public Texture2D fontAsset;

            public ASCIIShaderData(
                int numberOfCharacters,
                Vector2Int resolution,
                float fontRatio,
                Color fontColor,
                float fontColorStrength,
                Color backingColor,
                float backingColorStrength,
                Texture2D fontAsset
            )
            {
                this.numberOfCharacters = numberOfCharacters;
                this.resolution = new Vector4(resolution.x, resolution.y, 0, 0);
                this.fontRatio = fontRatio;
                this.fontColor = fontColor;
                this.fontColorStrength = fontColorStrength;
                this.backingColor = backingColor;
                this.backingColorStrength = backingColorStrength;
                this.fontAsset = fontAsset;
            }
        }

        private static class ShaderParams
        {
            public static int numberOfCharacters = Shader.PropertyToID("_ASCIICharacterCount");
            public static int resolution = Shader.PropertyToID("_ASCIIResolution");
            public static int inputTex = Shader.PropertyToID("_ASCIIInputTex");
            public static int fontRatio = Shader.PropertyToID("_ASCIIFontRatio");
            public static int fontColor = Shader.PropertyToID("_ASCIIFontColor");
            public static int fontColorStrength = Shader.PropertyToID("_ASCIIFontColorStrength");
            public static int backingColor = Shader.PropertyToID("_ASCIIBackingColor");
            public static int backingColorStrength = Shader.PropertyToID("_ASCIIBackingColorStrength");
            public static int fontAsset = Shader.PropertyToID("_ASCIIFontAsset");
        }

        public Vector2Int GetAspectRatio(AspectRatio aspectRatioDesc, Vector2Int customAspectRatio, bool flip)
        {
            Vector2Int temp = GetAspectRatio(aspectRatioDesc, customAspectRatio);
            if (flip)
                temp = new Vector2Int(temp.y, temp.x);

            return temp;
        }

        public Vector2Int GetAspectRatio(AspectRatio aspectRatioDesc, Vector2Int customAspectRatio)
        {
            switch (aspectRatioDesc)
            {
                case AspectRatio.SixteenToNine:
                    return new Vector2Int(16, 9);
                case AspectRatio.SixteenToTen:
                    return new Vector2Int(16, 10);
                case AspectRatio.ThreeToTwo:
                    return new Vector2Int(3, 2);
                case AspectRatio.FourToThree:
                    return new Vector2Int(4, 3);
                case AspectRatio.FiveToFour:
                    return new Vector2Int(5, 4);
                case AspectRatio.OneToOne:
                    return new Vector2Int(1, 1);
                default:
                    return new Vector2Int(1, 1);
            }
        }

        public enum AspectRatio
        {
            [InspectorName("16:9")]
            SixteenToNine,

            [InspectorName("16:10")]
            SixteenToTen,

            [InspectorName("3:2")]
            ThreeToTwo,

            [InspectorName("4:3")]
            FourToThree,

            [InspectorName("5:4")]
            FiveToFour,

            [InspectorName("1:1")]
            OneToOne
        }
    }
}
