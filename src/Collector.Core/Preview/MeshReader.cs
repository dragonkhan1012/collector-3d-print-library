using System.Globalization;
using System.Text;

namespace Collector.Core.Preview;

public static class MeshReader
{
    public const int MaximumTriangles = 500_000;
    public const long MaximumBytes = 128L*1024*1024;
    public static bool IsSupported(string path) => Path.GetExtension(path).ToLowerInvariant() is ".stl" or ".obj";
    public static Mesh3D Read(string path,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        using var stream=File.OpenRead(path);
        if (stream.Length>MaximumBytes) throw new InvalidDataException("Файл больше 128 МБ. Для просмотра выберите облегчённую модель.");
        if (!IsSupported(path)) throw new InvalidDataException("3D-просмотр пока поддерживает STL и OBJ.");
        var triangles=Path.GetExtension(path).Equals(".obj",StringComparison.OrdinalIgnoreCase)
            ? ReadObj(stream,token) : ReadStl(stream,token);
        if (triangles.Count==0) throw new InvalidDataException("В файле не найдены треугольники.");
        return new(triangles.ToArray());
    }
    private static void Add(List<Triangle3> target,Vec3 a,Vec3 b,Vec3 c)
    {
        if (!a.IsValid || !b.IsValid || !c.IsValid) throw new InvalidDataException("Некорректные координаты модели.");
        if(target.Count>=MaximumTriangles) throw new InvalidDataException("Больше 500 000 треугольников. Для просмотра выберите облегчённую модель.");
        target.Add(new(a,b,c));
    }
    private static List<Triangle3> ReadStl(Stream stream,CancellationToken token)
    {
        var triangles=new List<Triangle3>();
        using var binary=new BinaryReader(stream,Encoding.UTF8,true);
        if(stream.Length>=84)
        {
            stream.Position=80;var count=binary.ReadUInt32();
            // A binary STL header may itself begin with 'solid'. Use the record length.
            if(84L+50L*count==stream.Length)
            {
                if(count>MaximumTriangles) throw new InvalidDataException("Больше 500 000 треугольников. Выберите облегчённую модель.");
                Vec3 Vertex()=>new(binary.ReadSingle(),binary.ReadSingle(),binary.ReadSingle());
                for(var i=0;i<count;i++)
                {
                    token.ThrowIfCancellationRequested();
                    stream.Position+=12;var a=Vertex();var b=Vertex();var c=Vertex();binary.ReadUInt16();Add(triangles,a,b,c);
                }
                return triangles;
            }
        }
        stream.Position=0;
        using var text=new StreamReader(stream,Encoding.UTF8,true,4096,true);
        var vertices=new List<Vec3>(3);var inFacet=false;var sawSolid=false;
        while(text.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            if(line.Length>65536) throw new InvalidDataException("Слишком длинная строка STL.");
            var parts=line.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            if(parts.Length==0) continue;
            switch(parts[0].ToLowerInvariant())
            {
                case "solid": sawSolid=true;break;
                case "facet": if(inFacet) throw new InvalidDataException("Нарушена структура STL.");inFacet=true;vertices.Clear();break;
                case "vertex":
                    if(!inFacet || parts.Length!=4 || vertices.Count>=3) throw new InvalidDataException("Некорректная грань STL.");
                    vertices.Add(ParseVertex(parts));break;
                case "endfacet":
                    if(!inFacet || vertices.Count!=3) throw new InvalidDataException("Неполная грань STL.");
                    Add(triangles,vertices[0],vertices[1],vertices[2]);inFacet=false;break;
                case "outer": case "endloop": case "endsolid":break;
                default: throw new InvalidDataException("Не удалось прочитать STL: неизвестная или повреждённая запись.");
            }
        }
        if(!sawSolid || inFacet) throw new InvalidDataException("STL обрезан или повреждён.");
        return triangles;
    }
    private static Vec3 ParseVertex(string[] parts)
    {
        double Number(int i)
        {
            if(!double.TryParse(parts[i],NumberStyles.Float,CultureInfo.InvariantCulture,out var value))
                throw new InvalidDataException("Некорректное число в модели.");
            return value;
        }
        var result=new Vec3(Number(1),Number(2),Number(3));
        if(!result.IsValid) throw new InvalidDataException("В модели есть бесконечные или недопустимые координаты.");
        return result;
    }
    private static List<Triangle3> ReadObj(Stream stream,CancellationToken token)
    {
        var vertices=new List<Vec3>();var triangles=new List<Triangle3>();
        using var text=new StreamReader(stream,Encoding.UTF8,true,4096,true);
        while(text.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            if(line.Length>65536) throw new InvalidDataException("Слишком длинная строка OBJ.");
            var comment=line.IndexOf('#');if(comment>=0) line=line[..comment];
            var parts=line.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            if(parts.Length==0) continue;
            if(parts[0]=="v")
            {
                if(parts.Length<4) throw new InvalidDataException("Неполная вершина OBJ.");
                if(vertices.Count>=1_000_000) throw new InvalidDataException("Слишком много вершин OBJ.");
                vertices.Add(ParseVertex(parts));
            }
            else if(parts[0]=="f")
            {
                if(parts.Length<4 || parts.Length>513) throw new InvalidDataException("Недопустимая грань OBJ.");
                Vec3 Index(string value)
                {
                    var indexText=value.Split('/')[0];
                    if(!int.TryParse(indexText,NumberStyles.Integer,CultureInfo.InvariantCulture,out var index) || index==0)
                        throw new InvalidDataException("Некорректный индекс OBJ.");
                    long actual=index>0 ? (long)index-1 : (long)vertices.Count+index;
                    if(actual<0 || actual>=vertices.Count) throw new InvalidDataException("Грань ссылается на отсутствующую вершину OBJ.");
                    return vertices[(int)actual];
                }
                var first=Index(parts[1]);var previous=Index(parts[2]);
                for(var i=3;i<parts.Length;i++) { var next=Index(parts[i]);Add(triangles,first,previous,next);previous=next; }
            }
            // Materials/textures and external references are intentionally not opened.
        }
        return triangles;
    }
}
