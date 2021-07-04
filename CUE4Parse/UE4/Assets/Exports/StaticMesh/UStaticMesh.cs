using System;
using System.Collections.Generic;
using System.IO;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.Meshes;
using CUE4Parse.UE4.Objects.RenderCore;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using Serilog;

namespace CUE4Parse.UE4.Assets.Exports.StaticMesh
{
    public class UStaticMesh : UObject
    {
        private const int MAX_MESH_UV_SETS = 8;
        
        public bool bCooked { get; private set; }
        public FGuid LightingGuid { get; private set; }
        public FPackageIndex[] Sockets { get; private set; } // Lazy<UObject?>[]
        public FStaticMeshRenderData? RenderData { get; private set; }
        public FStaticMaterial[]? StaticMaterials { get; private set; }
        public Lazy<UMaterialInterface?>[]? Materials { get; private set; }

        public override void Deserialize(FAssetArchive Ar, long validPos)
        {
            base.Deserialize(Ar, validPos);

            var stripDataFlags = Ar.Read<FStripDataFlags>();
            bCooked = Ar.ReadBoolean();
            var bodySetup = Ar.ReadObject<UObject>();
            var navCollision = Ar.Ver >= UE4Version.VER_UE4_STATIC_MESH_STORE_NAV_COLLISION ? Ar.ReadObject<UObject>() : null;

            if (!stripDataFlags.IsEditorDataStripped())
            {
                throw new NotImplementedException("Static Mesh with Editor Data not implemented yet");
                // if (Ar.Ver < UE4Version.VER_UE4_DEPRECATED_STATIC_MESH_THUMBNAIL_PROPERTIES_REMOVED)
                // {
                //     var dummyThumbnailAngle = Ar.Read<FRotator>();
                //     var dummyThumbnailDistance = Ar.Read<float>();
                // }
                // var highResSourceMeshName = Ar.ReadString();
                // var highResSourceMeshCRC = Ar.Read<uint>();
            }

            LightingGuid = Ar.Read<FGuid>(); // LocalLightingGuid
            Sockets = Ar.ReadArray(() => new FPackageIndex(Ar));

            RenderData = new FStaticMeshRenderData(Ar, bCooked);

            if (bCooked & Ar.Game >= EGame.GAME_UE4_20)
            {
                var hasOccluderData = Ar.ReadBoolean();
                if (hasOccluderData)
                {
                    Ar.ReadArray<FVector>(); // Vertices
                    Ar.ReadArray<ushort>();  // Indics
                }
            }

            if (Ar.Game >= EGame.GAME_UE4_14)
            {
                var hasSpeedTreeWind = Ar.ReadBoolean();
                if (hasSpeedTreeWind)
                {
                    Ar.Seek(validPos, SeekOrigin.Begin);
                    return;
                }
                
                if (FEditorObjectVersion.Get(Ar) >= FEditorObjectVersion.Type.RefactorMeshEditorMaterials)
                {
                    // UE4.14+ - "Materials" are deprecated, added StaticMaterials
                    StaticMaterials = Ar.ReadArray(() => new FStaticMaterial(Ar));
                }
            }

            if (StaticMaterials != null && StaticMaterials.Length != 0)
            {
                Materials = new Lazy<UMaterialInterface?>[StaticMaterials.Length];
                for (var i = 0; i < Materials.Length; i++)
                {
                    Materials[i] = StaticMaterials[i].MaterialInterface;
                }
            }
        }

        protected internal override void WriteJson(JsonWriter writer, JsonSerializer serializer)
        {
            base.WriteJson(writer, serializer);

            writer.WritePropertyName("LightingGuid");
            serializer.Serialize(writer, LightingGuid);

            writer.WritePropertyName("RenderData");
            serializer.Serialize(writer, RenderData);
        }

        public CStaticMesh? Convert()
        {
            if (RenderData == null) return null;
            
            var lods = new List<CStaticMeshLod>();
            var numLods = RenderData.LODs.Length;
            for (var i = 0; i < numLods; i++)
            {
                if (RenderData.LODs[i] is not
                {
                    VertexBuffer: not null,
                    PositionVertexBuffer: not null,
                    ColorVertexBuffer: not null,
                    IndexBuffer: not null
                } srcLod) continue;
                
                var numTexCoords = srcLod.VertexBuffer.NumTexCoords;
                var numVerts = srcLod.PositionVertexBuffer.Verts.Length;
                if (numVerts == 0 && numTexCoords == 0 && i < numLods - 1) {
                    Log.Logger.Debug($"LOD {i} is stripped, skipping...");
                    continue;
                }

                if (numTexCoords > MAX_MESH_UV_SETS)
                    throw new ParserException($"Static mesh has too many UV sets ({numTexCoords})");

                var lod = new CStaticMeshLod();
                lods.Add(lod);
                lod.NumTexCoords = numTexCoords;
                lod.HasNormals = true;
                lod.HasTangents = true;

                var sections = new CMeshSection[srcLod.Sections.Length];
                for (var j = 0; j < sections.Length; j++)
                {
                    sections[j] = new CMeshSection(Materials?[srcLod.Sections[j].MaterialIndex], srcLod.Sections[j].FirstIndex, srcLod.Sections[j].NumTriangles);
                }
                lod.Sections = new Lazy<CMeshSection[]>(sections);

                lod.AllocateVerts(numVerts);
                if (srcLod.ColorVertexBuffer.NumVertices != 0)
                    lod.AllocateVertexColorBuffer();

                for (var j = 0; j < numVerts; j++)
                {
                    var suv = srcLod.VertexBuffer.UV[j];
                    if (suv.Normal[1].Data != 0)
                        throw new ParserException("Not implemented: should only be used in UE3");
                    
                    var v = lod.Verts[j];
                    v.Position = srcLod.PositionVertexBuffer.Verts[j];
                    v.Normal.Data = suv.Normal[2].Data;
                    v.Tangent.Data = suv.Normal[0].Data;
                    v.UV.U = suv.UV[0].U;
                    v.UV.V = suv.UV[0].V;
                    
                    for (var k = 1; k < numTexCoords; k++)
                    {
                        lod.ExtraUV.Value[k - 1][j].U = suv.UV[k].U;
                        lod.ExtraUV.Value[k - 1][j].V = suv.UV[k].V;
                    }

                    if (srcLod.ColorVertexBuffer.NumVertices != 0)
                        lod.VertexColors[j] = srcLod.ColorVertexBuffer.Data[j];
                }

                lod.Indices = new Lazy<FRawStaticIndexBuffer>(srcLod.IndexBuffer);
            }

            var ret = new CStaticMesh(this, lods);
            ret.FinalizeMesh();
            return ret;
        }
    }
}