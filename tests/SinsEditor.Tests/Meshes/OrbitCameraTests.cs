using System.Numerics;
using SinsEditor.Core.Meshes;
using SinsEditor.Viewer;

namespace SinsEditor.Tests.Meshes;

public sealed class OrbitCameraTests
{
    private static readonly MeshData Mesh = MeshData.Parse(["MeshData", "\tBoundingRadius 50.0", "\tMaxBoundingExtents [ 10.0 5.0 40.0 ]",
        "\tMinBoundingExtents [ -10.0 -5.0 -40.0 ]"]);

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 0.01f, $"expected {expected}, was {actual}");

    [Fact]
    public void Top_down_looks_from_above_with_the_bow_up()
    {
        var camera = OrbitCamera.TopDown(Mesh);
        Assert.True(camera.Direction.Y > 0.999f);
        // The eye sits a hair behind the target, so the screen's up (world +Y projected) is the bow (+Z).
        Assert.True(camera.Direction.Z < 0);
    }

    [Fact]
    public void A_wider_field_of_view_frames_from_closer()
    {
        Assert.True(OrbitCamera.Frame(Mesh, 90).Distance < OrbitCamera.Frame(Mesh, 45).Distance);
        Assert.Equal(90f, OrbitCamera.Frame(Mesh, 90).FieldOfView);
    }

    [Fact]
    public void Rotating_turns_the_eye_around_the_target()
    {
        var front = OrbitCamera.Front(Mesh);
        Near(Vector3.UnitZ, front.Direction);
        // 90° around Y: from the bow (+Z) to the right side (+X).
        var turned = front.Rotated(Vector3.UnitY, 90);
        Near(Vector3.UnitX, turned.Direction);
        Assert.Equal(front.Target, turned.Target);
        Assert.Equal(front.Distance, turned.Distance);
        // Past straight up the pitch stops.
        Assert.True(front.Rotated(Vector3.UnitX, -120).Pitch <= OrbitCamera.MaxPitch);
    }

    [Fact]
    public void Moving_shifts_eye_and_target_together()
    {
        var camera = OrbitCamera.Frame(Mesh);
        var moved = camera.Moved(new Vector3(0, 0, 25));
        Near(camera.Target + new Vector3(0, 0, 25), moved.Target);
        Near(camera.Eye + new Vector3(0, 0, 25), moved.Eye);
    }
}
