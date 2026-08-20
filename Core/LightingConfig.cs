// ═══════════════════════════════════════════════════════════════════════════
//  LightingConfig.cs — UI-editable snapshot of all lighting parameters
//
//  Threading: this is the bridge between the UI thread (writes) and the game
//  thread (reads). It is stored in GameSettings via atomic REFERENCE SWAP —
//  the UI clones the current config, mutates the copy, and assigns it back, so
//  the game thread always reads a fully-consistent snapshot (never a torn mix
//  of old and new field values). See GameSettings.Lighting.
//
//  Colours are packed 0xRRGGBB ints, matching the rest of the draw pipeline.
// ═══════════════════════════════════════════════════════════════════════════

namespace EDes
{
    // ── One configurable point light (spotlight) ──────────────────────────────
    public sealed class LightConfig
    {
        public bool  Enabled   = true;
        public float X         = 0f;
        public float Y         = 0f;
        public float Z         = -2f;
        public float Radius    = 16f;
        public float Intensity = 0.8f;
        public int   Color     = 0xFFFFFF;

        public LightConfig Clone() => (LightConfig)MemberwiseClone();
    }

    // ── Full lighting scene config ────────────────────────────────────────────
    public sealed class LightingConfig
    {
        // Global / combined-additive controls
        public bool  Enabled    = true;    // master toggle (off = flat passthrough)
        public float Ambient    = 0.12f;   // additive floor so unlit faces aren't black
        public float Brightness = 1.0f;    // overall multiplier
        public bool  UseGpu     = false;   // offload the N·L lighting to the GPU (ComputeSharp)

        // Post-lighting colour adjustments (applied to the final lit colour)
        public bool  BoostDark     = false;  // lift dark voxels toward white
        public float BoostStrength = 0.5f;   // 0..1 — how strongly darks are lifted
        public bool  CullBlack     = false;  // skip pure-black lit voxels (saves draw work)

        // "Simple lighting" — a single directional sun (additive with the spotlights)
        public bool  SunEnabled   = false;
        public float SunDirX      = 0f;
        public float SunDirY      = -1f;
        public float SunDirZ      = 0.3f;
        public float SunIntensity = 1.0f;
        public int   SunColor     = 0xFFFFFF;

        // Four configurable spotlights (point lights)
        public LightConfig[] Spots;

        public LightingConfig()
        {
            // Four white point lights near the corners of the volume — the same
            // symmetric starting point the engine used before it was UI-driven.
            Spots = new[]
            {
                new LightConfig { X =  4f, Y =  4f, Z = -2f, Color = 0xFFFFFF, Intensity = 0.8f, Radius = 16f },
                new LightConfig { X = -4f, Y =  4f, Z = -2f, Color = 0xFFFFFF, Intensity = 0.8f, Radius = 16f },
                new LightConfig { X =  4f, Y = -4f, Z = -2f, Color = 0xFFFFFF, Intensity = 0.8f, Radius = 16f },
                new LightConfig { X = -4f, Y = -4f, Z = -2f, Color = 0xFFFFFF, Intensity = 0.8f, Radius = 16f },
            };
        }

        public LightingConfig Clone()
        {
            var c = (LightingConfig)MemberwiseClone();
            c.Spots = new LightConfig[Spots.Length];
            for (int i = 0; i < Spots.Length; i++)
                c.Spots[i] = Spots[i].Clone();
            return c;
        }
    }
}
