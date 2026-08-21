using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using LibreLancer.ContentEdit;
using LibreLancer.ContentEdit.AssetDeduplication;
using LibreLancer.Data;
using Xunit;

public class AssetDeduplicationTests
{
    [Fact]
    public void TextureHashUsesDecodedPixelsAcrossTgaEncodings()
    {
        var raw = new LUtfNode
        {
            Name = "MIPS",
            Data = Tga24(2, [1, 2, 3, 1, 2, 3])
        };
        var rle = new LUtfNode
        {
            Name = "MIPS",
            Data = Tga24(10, [0x81, 1, 2, 3])
        };

        Assert.Equal(
            UtfAssetRewriter.HashTextureNode(raw),
            UtfAssetRewriter.HashTextureNode(rle));
    }

    [Fact]
    public void MaterialHashUsesCanonicalTextureName()
    {
        var first = MaterialNode("first", "old_texture.dds");
        var second = MaterialNode("second", "renamed_texture.dds");

        var firstMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["old_texture.dds"] = "ll_tex_1234.dds"
        };
        var secondMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["renamed_texture.dds"] = "ll_tex_1234.dds"
        };

        Assert.Equal(
            UtfAssetRewriter.HashMaterialNode(first, firstMap),
            UtfAssetRewriter.HashMaterialNode(second, secondMap));
    }

    [Fact]
    public void VmeshMaterialCrcsAreRewrittenInMeshHeaders()
    {
        var data = new byte[16 + (12 * 2)];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8, 2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), 0x01020304);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28, 4), 0xAABBCCDD);

        var changed = UtfAssetRewriter.RewriteVmeshMaterialCrcs(
            data,
            new Dictionary<uint, uint>
            {
                [0x01020304] = 0x11223344
            });

        Assert.Equal(1, changed);
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16, 4)));
        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28, 4)));
    }

    [Fact]
    public void EmbeddedMaterialAndTextureLibrariesCanBeRemoved()
    {
        var root = new LUtfNode { Name = "/", Children = new List<LUtfNode>() };
        AddLibrary(root, "Material Library");
        AddLibrary(root, "Texture Library");
        root.Children.Add(new LUtfNode { Name = "VMeshLibrary", Parent = root, Children = new List<LUtfNode>() });

        var removed = UtfAssetRewriter.RemoveEmbeddedLibraries(root);

        Assert.Equal(2, removed);
        Assert.DoesNotContain(root.Children, x => x.Name.Equals("Material Library", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(root.Children, x => x.Name.Equals("Texture Library", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(root.Children, x => x.Name.Equals("VMeshLibrary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MaterialAnimationNamesFollowMaterialRenames()
    {
        var root = new LUtfNode { Name = "/", Children = new List<LUtfNode>() };
        var animations = new LUtfNode { Name = "MaterialAnim", Parent = root, Children = new List<LUtfNode>() };
        animations.Children.Add(new LUtfNode
        {
            Name = "old_material",
            Parent = animations,
            Children = new List<LUtfNode>()
        });
        root.Children.Add(animations);

        var changed = UtfAssetRewriter.RewriteMaterialNames(
            root,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["old_material"] = "ll_mat_new"
            });

        Assert.Equal(1, changed);
        Assert.Equal("ll_mat_new", animations.Children[0].Name);
    }

    private static LUtfNode MaterialNode(string name, string texture)
    {
        var material = new LUtfNode { Name = name, Children = new List<LUtfNode>() };
        material.Children.Add(LUtfNode.StringNode(material, "Type", "DcDt"));
        material.Children.Add(LUtfNode.StringNode(material, "Dt_name", texture));
        material.Children.Add(LUtfNode.IntNode(material, "Dt_flags", 64));
        material.Children.Add(new LUtfNode { Name = "Dc", Parent = material, Data = [1, 2, 3, 4] });
        return material;
    }

    private static void AddLibrary(LUtfNode root, string name)
    {
        root.Children.Add(new LUtfNode
        {
            Name = name,
            Parent = root,
            Children = new List<LUtfNode>
            {
                new() { Name = "placeholder", Parent = root }
            }
        });
    }

    private static byte[] Tga24(byte imageType, byte[] payload)
    {
        var result = new byte[18 + payload.Length];
        result[2] = imageType;
        result[12] = 2;
        result[16] = 24;
        payload.CopyTo(result, 18);
        return result;
    }
}
