#if TOOLS
using Godot;
using System.Collections.Generic;

[Tool]
[GlobalClass]
public partial class BlendPostImport : EditorScenePostImport
{
    private class ItemData
    {
        public string Name;
        public Transform3D Transform;
        public Mesh Mesh;
        public Godot.Collections.Array Shapes;
        public NavigationMesh NavMesh;
        public Transform3D NavMeshTransform;
    }

    private static (NavigationMesh, Transform3D) FindNavigationMesh(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is NavigationRegion3D nr)
            {
                return (nr.NavigationMesh, nr.Transform);
            }
            else if (child is Node3D)
            {
                var result = FindNavigationMesh(child);
                if (result.Item1 != null) return result;
            }
        }
        return (null, Transform3D.Identity);
    }

    public override GodotObject _PostImport(Node sceneNode)
    {
        try
        {
            string sourcePath = GetSourceFile();
            string savePath = sourcePath.GetBaseName() + ".meshlib";

            GD.Print($"[BlendPostImport] Processing: {sourcePath}");

            // Check project setting for whether to overwrite previews on reimport
            bool overwritePreviews = true;
            if (ProjectSettings.HasSetting("blend_to_meshlibrary/overwrite_previews_on_reimport"))
            {
                overwritePreviews = ProjectSettings.GetSetting("blend_to_meshlibrary/overwrite_previews_on_reimport").AsBool();
            }

            // Load existing library to preserve previews (only if not overwriting)
            Dictionary<string, Texture2D> existingPreviews = new();

            if (!overwritePreviews)
            {
                try
                {
                    if (ResourceLoader.Exists(savePath))
                    {
                        var existingLibrary = ResourceLoader.Load<MeshLibrary>(savePath, cacheMode: ResourceLoader.CacheMode.Ignore);
                        if (existingLibrary != null)
                        {
                            foreach (int id in existingLibrary.GetItemList())
                            {
                                var name = existingLibrary.GetItemName(id);
                                var preview = existingLibrary.GetItemPreview(id);
                                if (!string.IsNullOrEmpty(name) && preview != null)
                                {
                                    existingPreviews[name] = preview;
                                }
                            }
                            GD.Print($"[BlendPostImport] Preserving {existingPreviews.Count} existing previews.");
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    GD.PrintErr($"[BlendPostImport] Warning: Could not load existing library: {ex.Message}");
                }
            }
            else
            {
                GD.Print("[BlendPostImport] Overwrite previews enabled - existing previews will be discarded.");
            }

            var meshLibrary = new MeshLibrary();
            var children = sceneNode.GetChildren();

            // Collect items first to allow reordering (ensuring ID 0 has a mesh)
            var collectedItems = new List<ItemData>();

            foreach (var child in children)
            {
                if (child is not Node3D node3d) continue;
                if (!node3d.Visible) continue;

                // Mesh
                MeshInstance3D meshInstance = node3d as MeshInstance3D;
                if (meshInstance == null)
                {
                    foreach (var sub in node3d.GetChildren())
                    {
                        if (sub is MeshInstance3D mi)
                        {
                            meshInstance = mi;
                            break;
                        }
                    }
                }

                // Collision
                var shapes = new Godot.Collections.Array();
                FindCollisionShapes(node3d, shapes, Transform3D.Identity);

                // Navigation
                var (navMesh, navXform) = FindNavigationMesh(node3d);

                // Skip empty nodes (no mesh, no collision, no navigation)
                if (meshInstance == null && shapes.Count == 0 && navMesh == null)
                {
                    continue;
                }

                // Check for NaN/Infinite transforms which can cause "instance is null" errors in the renderer
                if (!node3d.Transform.Origin.IsFinite() ||
                    !node3d.Transform.Basis.Column0.IsFinite() ||
                    !node3d.Transform.Basis.Column1.IsFinite() ||
                    !node3d.Transform.Basis.Column2.IsFinite())
                {
                    GD.PrintErr($"[BlendPostImport] Error: Item '{node3d.Name}' has a non-finite (NaN/Inf) transform. Resetting to Identity.");
                    node3d.Transform = Transform3D.Identity;
                }

                var itemData = new ItemData
                {
                    Name = node3d.Name,
                    Transform = node3d.Transform,
                    Shapes = shapes,
                    NavMesh = navMesh,
                    NavMeshTransform = navXform
                };

                if (meshInstance != null && meshInstance.Mesh != null)
                {
                    if (meshInstance.Mesh.GetSurfaceCount() > 0)
                        itemData.Mesh = meshInstance.Mesh;
                    else
                        GD.Print($"[BlendPostImport] Warning: Item '{node3d.Name}' has a mesh with 0 surfaces.");
                }

                collectedItems.Add(itemData);
            }

            // Ensure ID 0 has a mesh to prevent "instance is null" errors in GridMap
            int validMeshIndex = collectedItems.FindIndex(i => i.Mesh != null);
            if (validMeshIndex == -1 && collectedItems.Count > 0)
            {
                GD.PrintErr("[BlendPostImport] Warning: No items with valid meshes found in this library. GridMap cursor may cause errors.");
            }
            else if (validMeshIndex > 0)
            {
                GD.Print($"[BlendPostImport] Reordering: Swapping '{collectedItems[0].Name}' with '{collectedItems[validMeshIndex].Name}' to ensure ID 0 has a mesh.");
                var temp = collectedItems[0];
                collectedItems[0] = collectedItems[validMeshIndex];
                collectedItems[validMeshIndex] = temp;
            }

            // Write items to library
            int idCounter = 0;
            foreach (var item in collectedItems)
            {
                meshLibrary.CreateItem(idCounter);
                meshLibrary.SetItemName(idCounter, item.Name);
                meshLibrary.SetItemMeshTransform(idCounter, item.Transform);
                if (item.Mesh != null) meshLibrary.SetItemMesh(idCounter, item.Mesh);
                if (item.Shapes.Count > 0) meshLibrary.SetItemShapes(idCounter, item.Shapes);
                if (item.NavMesh != null)
                {
                    meshLibrary.SetItemNavigationMesh(idCounter, item.NavMesh);
                    meshLibrary.SetItemNavigationMeshTransform(idCounter, item.NavMeshTransform);
                }

                // Transfer preview from existing library if available
                if (existingPreviews.TryGetValue(item.Name, out var existingPreview))
                {
                    meshLibrary.SetItemPreview(idCounter, existingPreview);
                }

                idCounter++;
            }

            // Save the MeshLibrary
            Error err = ResourceSaver.Save(meshLibrary, savePath);

            int preservedCount = 0;
            foreach (int id in meshLibrary.GetItemList())
            {
                if (meshLibrary.GetItemPreview(id) != null) preservedCount++;
            }

            if (err != Error.Ok)
            {
                GD.PrintErr($"[BlendPostImport] Failed to save MeshLibrary to {savePath}: {err}");
            }
            else
            {
                int needsPreviews = idCounter - preservedCount;
                GD.Print($"[BlendPostImport] Saved {savePath}: {idCounter} items ({preservedCount} with previews, {needsPreviews} need generation).");
            }
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[BlendPostImport] Error during import: {ex.Message}\n{ex.StackTrace}");
        }

        return sceneNode;
    }

    private static void FindCollisionShapes(Node node, Godot.Collections.Array shapes, Transform3D parentXform)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is Node3D childNode3D)
            {
                Transform3D currentXform = parentXform * childNode3D.Transform;

                if (child is StaticBody3D)
                {
                    foreach (var sbChild in child.GetChildren())
                    {
                        if (sbChild is CollisionShape3D cs && cs.Shape != null)
                        {
                            shapes.Add(cs.Shape);
                            shapes.Add(currentXform * cs.Transform);
                        }
                    }
                }

                FindCollisionShapes(child, shapes, currentXform);
            }
        }
    }
}
#endif
