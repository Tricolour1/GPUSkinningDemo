using UnityEngine;

namespace GpuSkinningDemo
{
    // Arc-length lookup gives all render paths the same constant-speed root motion.
    public sealed class ParkMotionPath
    {
        private const int Segments = 192;
        private readonly Vector3[] points = new Vector3[Segments + 1];
        private readonly float[] distance = new float[Segments + 1];
        public float Length => distance[Segments];

        public ParkMotionPath(int kind)
        {
            for (int i = 0; i <= Segments; i++)
            {
                float t = (float)i / Segments;
                float a = t * Mathf.PI * 2;
                if (kind == 0) points[i] = new Vector3(-2.05f + Mathf.Cos(a) * 1.25f, 0, -1.15f + Mathf.Sin(a) * 1.25f);
                else if (kind == 1) points[i] = new Vector3(1.95f + Mathf.Sin(a) * 1.35f, 0, -1.15f + Mathf.Sin(2 * a) * 0.85f);
                else
                {
                    // Closed waypoint loop, equal physical speed on unequal-length segments.
                    Vector3[] corners = { new Vector3(-3.25f,0,1.15f), new Vector3(-3.25f,0,3), new Vector3(3.25f,0,3), new Vector3(3.25f,0,1.15f) };
                    float segment = t * 4;
                    int index = Mathf.Min(3, Mathf.FloorToInt(segment));
                    points[i] = Vector3.Lerp(corners[index], corners[(index + 1) % 4], segment - index);
                }
                if (i > 0) distance[i] = distance[i - 1] + Vector3.Distance(points[i - 1], points[i]);
            }
        }

        public void Evaluate(float traveled, out Vector3 position, out Quaternion rotation)
        {
            float target = Mathf.Repeat(traveled, Length);
            int low = 0, high = Segments;
            while (high - low > 1)
            {
                int mid = (low + high) / 2;
                if (distance[mid] < target) low = mid; else high = mid;
            }
            float fraction = Mathf.InverseLerp(distance[low], distance[high], target);
            position = Vector3.Lerp(points[low], points[high], fraction);
            rotation = Quaternion.LookRotation((points[high] - points[low]).normalized, Vector3.up);
        }

        public Vector3 Point(int index) => points[index];
        public int PointCount => points.Length;
    }
}
