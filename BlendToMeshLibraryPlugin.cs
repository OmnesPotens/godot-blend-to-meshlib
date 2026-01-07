#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

[Tool]
public partial class BlendToMeshLibraryPlugin : EditorPlugin
{
    // Use the same setting key as the PostImport script so they stay in sync
    private const string SETTING_OVERWRITE_PREVIEWS = "blend_to_meshlibrary/overwrite_previews_on_reimport";
    private const int PREVIEW_SIZE = 128;

    // Preview generation state
    private Queue<int> _previewItemQueue = new();
    private bool _isGeneratingPreviews = false;
    private MeshLibrary _targetMeshLibrary;

    // SubViewport resources
    private SubViewport _previewViewport;
    private Camera3D _previewCamera;
    private MeshInstance3D _previewMeshInstance;
    private DirectionalLight3D _previewLight;
    private DirectionalLight3D _previewFillLight;
    private int _currentPreviewItemId = -1;
    private int _frameWaitCount = 0;

    public override void _EnterTree()
    {
        // Register project setting
        if (!ProjectSettings.HasSetting(SETTING_OVERWRITE_PREVIEWS))
        {
            ProjectSettings.SetSetting(SETTING_OVERWRITE_PREVIEWS, true);
            ProjectSettings.SetInitialValue(SETTING_OVERWRITE_PREVIEWS, true);
        }

        // Connect to reimport signal
        var fileSystem = EditorInterface.Singleton.GetResourceFilesystem();
        fileSystem.ResourcesReimported += OnResourcesReimported;
    }

    public override void _ExitTree()
    {
        var fileSystem = EditorInterface.Singleton.GetResourceFilesystem();
        fileSystem.ResourcesReimported -= OnResourcesReimported;
        StopPreviewGeneration();
    }

    private void OnResourcesReimported(string[] resources)
    {
        // Prevent overlapping generation
        if (_isGeneratingPreviews) return;

        foreach (var path in resources)
        {
            // Check for source files that might have a corresponding .meshlib
            // The .meshlib is created/updated by the BlendPostImport script during the import process of these files.
            if (path.EndsWith(".blend", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
            {
                string meshLibPath = path.GetBaseName() + ".meshlib";

                // Only generate if the .meshlib exists (created by the PostImport script)
                if (ResourceLoader.Exists(meshLibPath))
                {
                    // Defer the call to ensure the import process is fully complete before we start modifying the resource
                    CallDeferred(MethodName.GeneratePreviewsForLibrary, meshLibPath);
                    return; // Handle one at a time
                }
            }
        }
    }

    private void GeneratePreviewsForLibrary(string path)
    {
        var library = ResourceLoader.Load<MeshLibrary>(path, cacheMode: ResourceLoader.CacheMode.Replace);
        if (library == null) return;

        var ids = library.GetItemList();
        if (ids.Length == 0) return;

        SetupPreviewViewport();
        _previewItemQueue.Clear();
        _targetMeshLibrary = library;

        foreach (int id in ids)
        {
            // Skip items without meshes
            if (library.GetItemMesh(id) != null)
            {
                _previewItemQueue.Enqueue(id);
            }
        }

        if (_previewItemQueue.Count > 0)
        {
            _isGeneratingPreviews = true;
            // Block signals to prevent the editor (and other plugins like ExtendedGridMap) 
            // from reacting to every single item update. This significantly improves performance 
            // and prevents log spam during batch generation.
            _targetMeshLibrary.SetBlockSignals(true);
            SetProcess(true);
            GD.Print($"[MeshLibraryPreviewPlugin] Starting preview generation for {path} ({_previewItemQueue.Count} items)...");
        }
        else
        {
            CleanupPreviewViewport();
        }
    }

    public override void _Process(double delta)
    {
        if (!_isGeneratingPreviews || _targetMeshLibrary == null) return;

        // Wait for GPU to render the frame
        // We need to wait at least 2 frames:
        // 1. Scene setup (RenderPreviewForItem)
        // 2. GPU Render
        // 3. Capture (CaptureCurrentPreview)
        if (_currentPreviewItemId >= 0)
        {
            _frameWaitCount++;
            if (_frameWaitCount >= 2)
            {
                CaptureCurrentPreview();
                _currentPreviewItemId = -1;
                _frameWaitCount = 0;
            }
            return;
        }

        // Process next item
        if (_previewItemQueue.Count > 0)
        {
            int itemId = _previewItemQueue.Dequeue();
            RenderPreviewForItem(itemId);
        }
        else
        {
            OnPreviewGenerationComplete();
        }
    }

    private void SetupPreviewViewport()
    {
        if (_previewViewport != null) return;

        // Create a dedicated SubViewport for rendering previews off-screen.
        // This avoids interfering with the main editor view.
        _previewViewport = new SubViewport
        {
            Size = new Vector2I(PREVIEW_SIZE, PREVIEW_SIZE),
            TransparentBg = true,
            OwnWorld3D = true,
            // We manually update via RenderTargetUpdateMode.Once to save performance
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled
        };

        var world3D = new World3D();
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0, 0, 0, 0),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.4f, 0.4f, 0.45f),
            AmbientLightEnergy = 0.8f
        };
        world3D.Environment = environment;
        _previewViewport.World3D = world3D;

        _previewCamera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            Near = 0.001f,
            Far = 1000.0f
        };
        _previewViewport.AddChild(_previewCamera);

        _previewMeshInstance = new MeshInstance3D();
        _previewViewport.AddChild(_previewMeshInstance);

        // Setup lighting to mimic Godot's default editor preview style
        // Main key light from top-right, closer to camera
        _previewLight = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(20, -30, 0),
            LightEnergy = 1.0f,
            LightColor = new Color(0.9f, 0.9f, 1.0f),
            ShadowEnabled = false
        };
        _previewViewport.AddChild(_previewLight);

        // Fill light from bottom-right to soften shadows
        _previewFillLight = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-150, -30, 0),
            LightEnergy = 0.2f,
            LightColor = new Color(0.9f, 0.9f, 1.0f),
            ShadowEnabled = false
        };
        _previewViewport.AddChild(_previewFillLight);

        AddChild(_previewViewport);
    }

    private void RenderPreviewForItem(int itemId)
    {
        try
        {
            var mesh = _targetMeshLibrary.GetItemMesh(itemId);
            if (mesh == null) return;

            _previewMeshInstance.Mesh = mesh;
            _previewMeshInstance.Transform = Transform3D.Identity;

            // Calculate the AABB (Axis Aligned Bounding Box) to center the camera
            var aabb = mesh.GetAabb();
            var center = aabb.GetCenter();
            float diagonal = aabb.Size.Length();
            if (diagonal < 0.001f) diagonal = 1.0f;

            // Position camera with reduced rotation and angle to match Godot native previews
            // Direction: X=0.5, Y=0.55, Z=1.0
            var cameraDir = new Vector3(0.5f, 0.55f, 1.0f).Normalized();
            _previewCamera.Position = center + cameraDir * diagonal * 2.0f;
            _previewCamera.LookAt(center, Vector3.Up);

            // Calculate the orthographic size required to fit the entire mesh within the viewport
            float maxExtent = CalculateOrthographicSizeForAabb(aabb, _previewCamera);
            _previewCamera.Size = maxExtent * 1.05f; // Add 5% margin for aesthetics

            // Trigger a single update of the viewport
            _previewViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
            _currentPreviewItemId = itemId;
            _frameWaitCount = 0;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MeshLibraryPreviewPlugin] Error rendering item {itemId}: {ex.Message}");
        }
    }

    // Projects the AABB corners onto the camera's view plane to determine the necessary orthographic size.
    private float CalculateOrthographicSizeForAabb(Aabb aabb, Camera3D camera)
    {
        var cameraTransform = camera.GlobalTransform;
        var cameraRight = cameraTransform.Basis.X.Normalized();
        var cameraUp = cameraTransform.Basis.Y.Normalized();

        var corners = new Vector3[8];
        corners[0] = new Vector3(aabb.Position.X, aabb.Position.Y, aabb.Position.Z);
        corners[1] = new Vector3(aabb.End.X, aabb.Position.Y, aabb.Position.Z);
        corners[2] = new Vector3(aabb.Position.X, aabb.End.Y, aabb.Position.Z);
        corners[3] = new Vector3(aabb.End.X, aabb.End.Y, aabb.Position.Z);
        corners[4] = new Vector3(aabb.Position.X, aabb.Position.Y, aabb.End.Z);
        corners[5] = new Vector3(aabb.End.X, aabb.Position.Y, aabb.End.Z);
        corners[6] = new Vector3(aabb.Position.X, aabb.End.Y, aabb.End.Z);
        corners[7] = new Vector3(aabb.End.X, aabb.End.Y, aabb.End.Z);

        float minU = float.MaxValue, maxU = float.MinValue;
        float minV = float.MaxValue, maxV = float.MinValue;

        foreach (var corner in corners)
        {
            var toCorner = corner - camera.GlobalPosition;
            // Project vector onto camera basis vectors
            float u = toCorner.Dot(cameraRight);
            float v = toCorner.Dot(cameraUp);

            minU = Mathf.Min(minU, u);
            maxU = Mathf.Max(maxU, u);
            minV = Mathf.Min(minV, v);
            maxV = Mathf.Max(maxV, v);
        }

        // Return the maximum dimension required to fit the object
        return Mathf.Max(maxU - minU, maxV - minV);
    }

    private void CaptureCurrentPreview()
    {
        try
        {
            var image = _previewViewport.GetTexture()?.GetImage();
            if (image != null)
            {
                // Create a persistent ImageTexture from the viewport capture
                var previewTexture = ImageTexture.CreateFromImage(image);
                _targetMeshLibrary.SetItemPreview(_currentPreviewItemId, previewTexture);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MeshLibraryPreviewPlugin] Error capturing item {_currentPreviewItemId}: {ex.Message}");
        }
    }

    private void OnPreviewGenerationComplete()
    {
        if (_targetMeshLibrary != null)
        {
            ResourceSaver.Save(_targetMeshLibrary);
            GD.Print($"[MeshLibraryPreviewPlugin] Previews generated and saved to {_targetMeshLibrary.ResourcePath}");
        }
        StopPreviewGeneration();
    }

    private void StopPreviewGeneration()
    {
        if (_targetMeshLibrary != null)
        {
            // Re-enable signals so the editor detects the changes
            _targetMeshLibrary.SetBlockSignals(false);
            // Emit a single changed signal to trigger updates (e.g. in ExtendedGridMap)
            _targetMeshLibrary.EmitChanged();
        }
        _isGeneratingPreviews = false;
        _previewItemQueue.Clear();
        _currentPreviewItemId = -1;
        _targetMeshLibrary = null;
        SetProcess(false);
        CleanupPreviewViewport();
    }

    private void CleanupPreviewViewport()
    {
        if (_previewViewport != null)
        {
            _previewViewport.QueueFree();
            _previewViewport = null;
            _previewCamera = null;
            _previewMeshInstance = null;
            _previewLight = null;
            _previewFillLight = null;
        }
    }
}
#endif
