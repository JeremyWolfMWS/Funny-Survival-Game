using UnityEngine;

public static class VoxelNoise
{
    /// <summary>Fractal Perlin noise normalized to 0..1.</summary>
    public static float FBm(float x, float z, float scale, int octaves, float persistence, float lacunarity, bool ridged = false)
    {
        float sum = 0f, amplitude = 1f, frequency = scale, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            float n = Mathf.Clamp01(Mathf.PerlinNoise(x * frequency + i * 31.7f, z * frequency + i * 47.3f));
            if (ridged)
            {
                n = 1f - Mathf.Abs(n * 2f - 1f);
                n *= n;
            }
            sum += n * amplitude;
            norm += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }
        return norm > 0f ? sum / norm : 0f;
    }

    /// <summary>
    /// How FBm values are spread out. Several octaves bunch up around 0.5, so fixed cuts like "below 0.3" hit far
    /// less of the world than they sound like. This samples the noise once and maps values to and from their share.
    /// </summary>
    public sealed class Spread
    {
        readonly float[] sorted;

        public Spread(int octaves, float persistence = 0.5f, float lacunarity = 2f, int samples = 8192)
        {
            var rng = new System.Random(9173 + octaves);
            sorted = new float[samples];
            for (int i = 0; i < samples; i++)
                sorted[i] = FBm((float)rng.NextDouble() * 1000f, (float)rng.NextDouble() * 1000f, 1f, octaves, persistence, lacunarity);
            System.Array.Sort(sorted);
        }

        /// <summary>The share of the world below v, 0..1. Evenly spread, unlike the noise itself.</summary>
        public float Even(float v)
        {
            int lo = 0, hi = sorted.Length - 1;
            if (v <= sorted[lo]) return 0f;
            if (v >= sorted[hi]) return 1f;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (sorted[mid] <= v) lo = mid; else hi = mid;
            }
            float span = sorted[hi] - sorted[lo];
            float t = span > 0f ? (v - sorted[lo]) / span : 0f;
            return (lo + t) / (sorted.Length - 1);
        }

        /// <summary>The noise value with this share of the world below it.</summary>
        public float Cut(float share)
        {
            float f = Mathf.Clamp01(share) * (sorted.Length - 1);
            int i = Mathf.Min((int)f, sorted.Length - 2);
            return Mathf.Lerp(sorted[i], sorted[i + 1], f - i);
        }
    }

    static readonly int[] perm = BuildPermutation();

    static int[] BuildPermutation()
    {
        var rng = new System.Random(12345);
        var p = new int[256];
        for (int i = 0; i < 256; i++) p[i] = i;
        for (int i = 255; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }
        var doubled = new int[512];
        for (int i = 0; i < 512; i++) doubled[i] = p[i & 255];
        return doubled;
    }

    /// <summary>3D Perlin noise, roughly -1..1. Offset the inputs for different seeds.</summary>
    public static float Perlin3(float x, float y, float z)
    {
        int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y), zi = Mathf.FloorToInt(z);
        float xf = x - xi, yf = y - yi, zf = z - zi;
        xi &= 255; yi &= 255; zi &= 255;
        float u = Fade(xf), v = Fade(yf), w = Fade(zf);

        int a = perm[xi] + yi, aa = perm[a] + zi, ab = perm[a + 1] + zi;
        int b = perm[xi + 1] + yi, ba = perm[b] + zi, bb = perm[b + 1] + zi;

        float x1 = Mathf.Lerp(Grad(perm[aa], xf, yf, zf), Grad(perm[ba], xf - 1, yf, zf), u);
        float x2 = Mathf.Lerp(Grad(perm[ab], xf, yf - 1, zf), Grad(perm[bb], xf - 1, yf - 1, zf), u);
        float y1 = Mathf.Lerp(x1, x2, v);
        x1 = Mathf.Lerp(Grad(perm[aa + 1], xf, yf, zf - 1), Grad(perm[ba + 1], xf - 1, yf, zf - 1), u);
        x2 = Mathf.Lerp(Grad(perm[ab + 1], xf, yf - 1, zf - 1), Grad(perm[bb + 1], xf - 1, yf - 1, zf - 1), u);
        return Mathf.Lerp(y1, Mathf.Lerp(x1, x2, v), w);
    }

    static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    static float Grad(int hash, float x, float y, float z)
    {
        int h = hash & 15;
        float u = h < 8 ? x : y;
        float v = h < 4 ? y : h == 12 || h == 14 ? x : z;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }
}
