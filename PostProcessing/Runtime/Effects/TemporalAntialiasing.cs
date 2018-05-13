using System;

namespace UnityEngine.Rendering.PostProcessing
{
    [Serializable]
    public sealed class TemporalAntialiasing
    {
        [Tooltip("The diameter (in texels) inside which jitter samples are spread. Smaller values result in crisper but more aliased output, while larger values result in more stable but blurrier output.")]
        [Range(0.1f, 1f)]
        public float jitterSpread = 0.75f;

        [Tooltip("Controls the amount of sharpening applied to the color buffer. High values may introduce dark-border artifacts.")]
        [Range(0f, 3f)]
        public float sharpness = 0.25f;

        [Tooltip("The blend coefficient for a stationary fragment. Controls the percentage of history sample blended into the final color.")]
        [Range(0f, 0.99f)]
        public float stationaryBlending = 0.95f;

        [Tooltip("The blend coefficient for a fragment with significant motion. Controls the percentage of history sample blended into the final color.")]
        [Range(0f, 0.99f)]
        public float motionBlending = 0.85f;

        // For custom jittered matrices - use at your own risks
        public Func<Camera, Vector2, Matrix4x4> jitteredMatrixFunc;

        public Vector2 jitter { get; private set; }

        enum Pass
        {
            SolverDilate,
            SolverDilateStereo,
            SolverNoDilate
        }

        readonly RenderTargetIdentifier[] m_Mrt = new RenderTargetIdentifier[2];
        bool m_ResetHistory = true;

        const int k_SampleCount = 8;
        public int sampleIndex { get; private set; }

        // Ping-pong between two history textures as we can't read & write the same target in the
        // same pass
        const int k_NumEyes = 2;
        const int k_NumHistoryTextures = 2;
        readonly RenderTexture[][] m_HistoryTextures = new RenderTexture[k_NumEyes][];

        int[] m_HistoryPingPong = new int [k_NumEyes];

        public bool IsSupported()
        {
            return SystemInfo.supportedRenderTargetCount >= 2
                && SystemInfo.supportsMotionVectors
#if !UNITY_2017_3_OR_NEWER
                && !RuntimeUtilities.isVREnabled
#endif
                && SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES2;
        }

        internal DepthTextureMode GetCameraFlags()
        {
            return DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
        }

        internal void ResetHistory()
        {
            m_ResetHistory = true;
        }

        Vector2 GenerateRandomOffset()
        {
            // The variance between 0 and the actual halton sequence values reveals noticeable instability
            // in Unity's shadow maps, so we avoid index 0.
            var offset = new Vector2(
                    HaltonSeq.Get((sampleIndex & 1023) + 1, 2) - 0.5f,
                    HaltonSeq.Get((sampleIndex & 1023) + 1, 3) - 0.5f
                );

            if (++sampleIndex >= k_SampleCount)
                sampleIndex = 0;

            return offset;
        }

        public Matrix4x4 GetJitteredProjectionMatrix(Camera camera)
        {
            Matrix4x4 cameraProj;
            jitter = GenerateRandomOffset();
            jitter *= jitterSpread;

            if (jitteredMatrixFunc != null)
            {
                cameraProj = jitteredMatrixFunc(camera, jitter);
            }
            else
            {
                cameraProj = camera.orthographic
                    ? RuntimeUtilities.GetJitteredOrthographicProjectionMatrix(camera, jitter)
                    : RuntimeUtilities.GetJitteredPerspectiveProjectionMatrix(camera, jitter);
            }

            jitter = new Vector2(jitter.x / camera.pixelWidth, jitter.y / camera.pixelHeight);
            return cameraProj;
        }

        public void ConfigureJitteredProjectionMatrix(PostProcessRenderContext context)
        {
            var camera = context.camera;
            camera.nonJitteredProjectionMatrix = camera.projectionMatrix;
            camera.projectionMatrix = GetJitteredProjectionMatrix(camera);
            camera.useJitteredProjectionMatrixForTransparentRendering = false;
        }

        // TODO: We'll probably need to isolate most of this for SRPs
        public void ConfigureStereoJitteredProjectionMatrices(PostProcessRenderContext context)
        {
#if  UNITY_2017_3_OR_NEWER
            var camera = context.camera;
            jitter = GenerateRandomOffset();
            jitter *= jitterSpread;

            for (var eye = Camera.StereoscopicEye.Left; eye <= Camera.StereoscopicEye.Right; eye++)
            {
                // This saves off the device generated projection matrices as non-jittered
                context.camera.CopyStereoDeviceProjectionMatrixToNonJittered(eye);
                var originalProj = context.camera.GetStereoNonJitteredProjectionMatrix(eye);

                // Currently no support for custom jitter func, as VR devices would need to provide
                // original projection matrix as input along with jitter 
                var jitteredMatrix = RuntimeUtilities.GenerateJitteredProjectionMatrixFromOriginal(context, originalProj, jitter);
                context.camera.SetStereoProjectionMatrix(eye, jitteredMatrix);
            }

            // jitter has to be scaled for the actual eye texture size, not just the intermediate texture size
            // which could be double-wide in certain stereo rendering scenarios
            jitter = new Vector2(jitter.x / context.screenWidth, jitter.y / context.screenHeight);
            camera.useJitteredProjectionMatrixForTransparentRendering = false;
#endif
        }

        void GenerateHistoryName(RenderTexture rt, int id, PostProcessRenderContext context)
        {
            rt.name = "Temporal Anti-aliasing History id #" + id;

            if (context.stereoActive)
                rt.name += " for eye " + context.xrActiveEye;
        }

        RenderTexture CheckHistory(int id, PostProcessRenderContext context)
        {
            int activeEye = context.xrActiveEye;

            if (m_HistoryTextures[activeEye] == null)
                m_HistoryTextures[activeEye] = new RenderTexture[k_NumHistoryTextures];

            var rt = m_HistoryTextures[activeEye][id];

            var desc = new RenderTextureDescriptor {
                dimension = TextureDimension.Tex2D,
                width = context.width,
                height = context.height,
                colorFormat = RenderTextureFormat.ARGBFloat,
                volumeDepth = 1,
                msaaSamples = 1,
                depthBufferBits = 0,
                useMipMap = false,
                enableRandomWrite = true
            };

            if (m_ResetHistory || rt == null || !rt.IsCreated())
            {
                RenderTexture.ReleaseTemporary(rt);

                rt = RenderTexture.GetTemporary(desc);
                GenerateHistoryName(rt, id, context);

                rt.filterMode = FilterMode.Bilinear;
                m_HistoryTextures[activeEye][id] = rt;

                context.command.BlitFullscreenTriangle(context.source, rt);
            }
            else if (rt.width != context.width || rt.height != context.height)
            {
                // On size change, simply copy the old history to the new one. This looks better
                // than completely discarding the history and seeing a few aliased frames.
                var rt2 =RenderTexture.GetTemporary(desc);
                GenerateHistoryName(rt2, id, context);

                rt2.filterMode = FilterMode.Bilinear;
                m_HistoryTextures[activeEye][id] = rt2;

                context.command.BlitFullscreenTriangle(rt, rt2);
                RenderTexture.ReleaseTemporary(rt);
            }

            return m_HistoryTextures[activeEye][id];
        }

        internal void Render(PostProcessRenderContext context)
        {
            var cmd = context.command;
            cmd.BeginSample("TemporalAntialiasing");

            int pp = m_HistoryPingPong[context.xrActiveEye];
            var historyRead = CheckHistory(++pp % 2, context);
            var historyWrite = CheckHistory(++pp % 2, context);
            m_HistoryPingPong[context.xrActiveEye] = ++pp % 2;

            var compute = context.resources.computeShaders.temporalAntialiasing;
            var kernel = context.camera.orthographic ? (int)Pass.SolverNoDilate : (int)Pass.SolverDilate; 

            var renderViewportScaleFactor = Shader.GetGlobalFloat("_RenderViewportScaleFactor");
            cmd.SetComputeFloatParam(compute, "_RenderViewportScaleFactor", renderViewportScaleFactor);
            cmd.SetComputeVectorParam(compute, "_ScaleOffsetRes", new Vector4(1, 0, context.width, context.height));
            cmd.SetComputeTextureParam(compute, kernel, "_CameraDepthTexture", BuiltinRenderTextureType.ResolvedDepth);
            cmd.SetComputeTextureParam(compute, kernel, "_CameraMotionVectorsTexture", BuiltinRenderTextureType.MotionVectors);

            //

            cmd.SetComputeVectorParam(compute, "_MainTex_TexelSize", 
                new Vector4(1f / context.width, 1f / context.height, context.width, context.height));
            cmd.SetComputeVectorParam(compute, "_CameraDepthTexture_TexelSize", 
                new Vector4(1f / context.width, 1f / context.height, context.width, context.height));

            //

            const float kMotionAmplification = 100f * 60f;
            cmd.SetComputeVectorParam(compute, ShaderIDs.Jitter, jitter);
            cmd.SetComputeFloatParam(compute, ShaderIDs.Sharpness, sharpness);
            cmd.SetComputeVectorParam(compute, ShaderIDs.FinalBlendParameters, new Vector4(stationaryBlending, motionBlending, kMotionAmplification, 0f));
            
            cmd.SetComputeTextureParam(compute, kernel, ShaderIDs.MainTex, context.source);

            cmd.SetComputeTextureParam(compute, kernel, ShaderIDs.HistoryTex, historyRead);

            // TODO: Account for different possible RenderViewportScale value from previous frame...
            int tmpRW = Shader.PropertyToID("tmpRW");
            cmd.GetTemporaryRT(tmpRW, context.width, context.height, 0, FilterMode.Bilinear, 
                context.sourceFormat, RenderTextureReadWrite.Default, 1, true);

            cmd.SetComputeTextureParam(compute, kernel, ShaderIDs.DestinationTex, tmpRW);
            cmd.SetComputeTextureParam(compute, kernel, ShaderIDs.OutputHistoryTex, historyWrite);
            
            int x = (context.width  + 7) / 8;
            int y = (context.height + 7) / 8;
            cmd.DispatchCompute(compute, kernel, x, y, 1);

            cmd.BlitFullscreenTriangle(tmpRW, context.destination);
            cmd.ReleaseTemporaryRT(tmpRW);

            cmd.EndSample("TemporalAntialiasing");

            m_ResetHistory = false;
        }

        internal void Release()
        {
            if (m_HistoryTextures != null)
            {
                for (int i = 0; i < m_HistoryTextures.Length; i++)
                {
                    if (m_HistoryTextures[i] == null)
                        continue;
                    
                    for (int j = 0; j < m_HistoryTextures[i].Length; j++)
                    {
                        RenderTexture.ReleaseTemporary(m_HistoryTextures[i][j]);
                        m_HistoryTextures[i][j] = null;
                    }

                    m_HistoryTextures[i] = null;
                }
            }

            sampleIndex = 0;
            m_HistoryPingPong[0] = 0;
            m_HistoryPingPong[1] = 0;
            
            ResetHistory();
        }
        
        static Vector2[] kSampleOffsets = new Vector2[] {
            new Vector2 ( -1.0f, -1.0f ),
            new Vector2 (  0.0f, -1.0f ),
            new Vector2 (  1.0f, -1.0f ),
            new Vector2 ( -1.0f,  0.0f ),
            new Vector2 (  0.0f,  0.0f ),
            new Vector2 (  1.0f,  0.0f ),
            new Vector2 ( -1.0f,  1.0f ),
            new Vector2 (  0.0f,  1.0f ),
            new Vector2 (  1.0f,  1.0f ),
        };

        static float[] s_Weights = new float[9];
        static float[] s_WeightsPlus = new float[5];

        void ComputeWeights(CommandBuffer cmd, ComputeShader compute)
        {
            float totalWeight = 0.0f;
            float totalWeightLow = 0.0f;
            float totalWeightPlus = 0.0f;

            for (int i = 0; i < 9; ++i)
            {
                float pixelOffsetX = kSampleOffsets[i][0] - jitter.x;
                float pixelOffsetY = kSampleOffsets[i][1] - jitter.y;

                pixelOffsetX *= jitterSpread;
                pixelOffsetY *= jitterSpread;

                if (true)
                {
                    s_Weights[i] = CatmullRom(pixelOffsetX) * CatmullRom(pixelOffsetY);
                    totalWeight += s_Weights[i];
                }
                else
                {
                    // Normal distribution, Sigma = 0.47
                    s_Weights[i] = Mathf.Exp(-2.29f * (pixelOffsetX * pixelOffsetX + pixelOffsetY * pixelOffsetY));
                    totalWeight += s_Weights[i];
                }
            }

            s_WeightsPlus[0] = s_Weights[1];
            s_WeightsPlus[1] = s_Weights[3];
            s_WeightsPlus[2] = s_Weights[4];
            s_WeightsPlus[3] = s_Weights[5];
            s_WeightsPlus[4] = s_Weights[7];
            totalWeightPlus = s_Weights[1] + s_Weights[3] + s_Weights[4] + s_Weights[5] + s_Weights[7];

            for (int i = 0; i < s_Weights.Length; ++i)
                s_Weights[i] /= totalWeight;
            for (int i = 0; i < s_WeightsPlus.Length; ++i)
                s_WeightsPlus[i] /= totalWeightPlus;

            cmd.SetComputeFloatParams(compute, "_SampleWeights", s_Weights);
            cmd.SetComputeFloatParams(compute, "_PlusWeights", s_Weights);
        }

        static float CatmullRom(float x)
        {
            float ax = Mathf.Abs(x);
            if (ax > 1.0f)
                return ((-0.5f * ax + 2.5f) * ax - 4.0f) * ax + 2.0f;
            else
                return (1.5f * ax - 2.5f) * ax * ax + 1.0f;
        }
    }
}
