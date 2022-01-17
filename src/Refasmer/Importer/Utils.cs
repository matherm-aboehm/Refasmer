using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace JetBrains.Refasmer
{
    public partial class MetadataImporter
    {
        private int GetNextToken(TableIndex index) => _builder.GetRowCount(index) + 1;

        private TypeDefinitionHandle NextTypeHandle() =>
            MetadataTokens.TypeDefinitionHandle(GetNextToken(TableIndex.TypeDef));

        private FieldDefinitionHandle NextFieldHandle() =>
            MetadataTokens.FieldDefinitionHandle(GetNextToken(TableIndex.Field));

        private MethodDefinitionHandle NextMethodHandle() =>
            MetadataTokens.MethodDefinitionHandle(GetNextToken(TableIndex.MethodDef));

        private ParameterHandle NextParameterHandle() => MetadataTokens.ParameterHandle(GetNextToken(TableIndex.Param));

        private EventDefinitionHandle NextEventHandle() =>
            MetadataTokens.EventDefinitionHandle(GetNextToken(TableIndex.Event));

        private PropertyDefinitionHandle NextPropertyHandle() =>
            MetadataTokens.PropertyDefinitionHandle(GetNextToken(TableIndex.Property));

        private static readonly Func<object, int?> RowId = MetaUtil.RowId;

        private EntityHandle FindMethod(string fullTypeName, string methodName, Func<BlobReader, bool> checkSignature, out MetadataReader arReader)
        {
            arReader = _reader;
            var typeRefHandle = _reader.TypeReferences
                .SingleOrDefault(h => _reader.GetFullname(h) == fullTypeName);

            if (!IsNil(typeRefHandle))
            {
                return _reader.MemberReferences
                    .Select(mrh => new { mrh, mr = _reader.GetMemberReference(mrh) })
                    .Where(x => x.mr.Parent == typeRefHandle)
                    .Where(x => _reader.GetString(x.mr.Name) == methodName)
                    .Where(x => checkSignature(_reader.GetBlobReader(x.mr.Signature)))
                    .Select(x => x.mrh)
                    .SingleOrDefault();
            }

            var typeDefHandle = _reader.TypeDefinitions
                .SingleOrDefault(h => _reader.GetFullname(h) == fullTypeName);

            if (!IsNil(typeDefHandle))
            {
                return _reader.GetTypeDefinition(typeDefHandle).GetMethods()
                    .Select(mdh => new { mdh, md = _reader.GetMethodDefinition(mdh) })
                    .Where(x => _reader.GetString(x.md.Name) == methodName)
                    .Where(x => checkSignature(_reader.GetBlobReader(x.md.Signature)))
                    .Select(x => x.mdh)
                    .SingleOrDefault();
            }

            MetadataReader reader;
            (reader, typeDefHandle) = _reader.AssemblyReferences
                .Select(h => GetMetadataReader(h))
                .SelectMany(r => r.TypeDefinitions, (r, h) => (r, h))
                .SingleOrDefault(td => td.r.GetFullname(td.h) == fullTypeName);

            if (!IsNil(typeDefHandle))
            {
                arReader = reader;
                return reader.GetTypeDefinition(typeDefHandle).GetMethods()
                    .Select(mdh => new { mdh, md = reader.GetMethodDefinition(mdh) })
                    .Where(x => reader.GetString(x.md.Name) == methodName)
                    .Where(x => checkSignature(reader.GetBlobReader(x.md.Signature)))
                    .Select(x => x.mdh)
                    .SingleOrDefault();
            }

            return default;
        }

        private MetadataReader GetMetadataReader(AssemblyReferenceHandle arHandle)
        {
            if (_asmRefReadersCache.TryGetValue(arHandle, out var metaReader))
                return metaReader;
            var ar = _reader.GetAssemblyReference(arHandle);
            var an = ar.GetAssemblyName();
            string location = an.CodeBase;
            if (string.IsNullOrEmpty(location))
            {
                if (an.CultureInfo == null) an.CultureInfo = CultureInfo.InvariantCulture;
                var assembly = Assembly.ReflectionOnlyLoad(an.FullName);
                location = assembly.Location;
            }
            else if (Uri.TryCreate(location, UriKind.Absolute, out var uri))
                location = uri.LocalPath;
            else
                throw new FileNotFoundException("Can't locate referenced assembly", an.FullName);
            var peReader = new PEReader(new FileStream(location, FileMode.Open, FileAccess.Read) /* stream closed by memory block provider within PEReader when the latter is disposed of */);
            disposables.Add(peReader);
            metaReader = peReader.GetMetadataReader();
            _asmRefReadersCache.Add(arHandle, metaReader);
            _readersAsmRefCache.Add(metaReader, arHandle);

            if (!metaReader.IsAssembly)
                Warning?.Invoke($"Dll has no assembly: {location}");
            return metaReader;
        }
    }
}