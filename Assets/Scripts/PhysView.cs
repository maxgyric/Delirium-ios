// Menu > Physics view: Delirium's own copy of the world drawn over the game's round every ragdoll, to see where they
// differ. Crosses where NM's ground is under a grid of rays from above (green: faces up, red: faces down, which pulls
// a limb in; yellow: a wall), its body parts where NM has them (blue; pink when touching something). The game's
// own picture is the map and the drawn body: a part off its limb, or green crosses off the ground you see, is the gap.
using System.Collections.Generic;
using UnityEngine;

public partial class Game
{
    bool physView;
    Material physMat;
    readonly List<Vector3> pvGround = new List<Vector3>(), pvFaces = new List<Vector3>(), pvParts = new List<Vector3>();
    readonly List<bool> pvTouch = new List<bool>();
    readonly List<(Vector3, Vector3, Color)> pvLines = new List<(Vector3, Vector3, Color)>();

    // LateUpdate: the lines for this frame (after Delirium moved the bodies)
    void PhysViewGather()
    {
        pvLines.Clear();
        if (!physView || nm == null) return;
        foreach (var m in men)
        {
            if (m == null || m.Body == null) continue;
            if (!nm.SandboxView(m.Body.transform, 3f, 0.3f, pvGround, pvFaces, pvParts, pvTouch)) continue;
            for (int i = 0; i < pvGround.Count; i++)
            {
                float up = pvFaces[i].y;
                var c = up > 0.3f ? new Color(0.2f, 1f, 0.3f, 0.9f) : up < -0.3f ? new Color(1f, 0.15f, 0.1f, 1f) : new Color(1f, 0.9f, 0.1f, 0.9f);
                Cross(pvGround[i], 0.06f, c, false);
            }
            for (int i = 0; i < pvParts.Count; i++)
                Cross(pvParts[i], 0.1f, pvTouch[i] ? new Color(1f, 0.3f, 0.9f, 1f) : new Color(0.2f, 0.6f, 1f, 1f), true);
        }
    }

    void Cross(Vector3 p, float r, Color c, bool tall)
    {
        pvLines.Add((p - Vector3.right * r, p + Vector3.right * r, c));
        pvLines.Add((p - Vector3.forward * r, p + Vector3.forward * r, c));
        if (tall) pvLines.Add((p - Vector3.up * r, p + Vector3.up * r, c));
    }

    void OnRenderObject()
    {
        if (!physView || pvLines.Count == 0 || Camera.current != cam) return;
        if (physMat == null)
        {
            var sh = Resources.Load<Shader>("DeliriumLines");
            if (sh == null) return;
            physMat = new Material(sh);
        }
        physMat.SetPass(0);
        GL.PushMatrix();
        GL.MultMatrix(Matrix4x4.identity);
        GL.Begin(GL.LINES);
        foreach (var (a, b, c) in pvLines)
        {
            GL.Color(c);
            GL.Vertex(a);
            GL.Vertex(b);
        }
        GL.End();
        GL.PopMatrix();
    }
}
