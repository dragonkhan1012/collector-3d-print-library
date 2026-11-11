namespace Collector.Core.Preview;

public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X*s,a.Y*s,a.Z*s);
    public double Length => Math.Sqrt(X*X+Y*Y+Z*Z);
    public static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
    public static double Dot(Vec3 a, Vec3 b) => a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z)
        && Math.Max(Math.Abs(X),Math.Max(Math.Abs(Y),Math.Abs(Z))) <= 1e100;
}
public readonly record struct Triangle3(Vec3 A, Vec3 B, Vec3 C, byte Material = 0);
public sealed record Mesh3D(Triangle3[] Triangles);
public enum UpAxis { Z, Y }

public sealed class PreviewScene
{
    public Triangle3[] Triangles { get; }
    public double CenterY { get; }
    public double Radius { get; }
    public int ModelTriangleCount { get; }
    internal Vec3[] Vertices { get; }
    internal RenderFace[] Faces { get; }
    internal readonly record struct RenderFace(int A,int B,int C,Vec3 Normal,byte Material);
    private PreviewScene(Triangle3[] triangles, double centerY, double radius, int count,CancellationToken token)
    {
        (Triangles,CenterY,Radius,ModelTriangleCount)=(triangles,centerY,radius,count);
        // STL repeats every vertex. Weld exact positions once, never alter printable geometry.
        var vertices=new List<Vec3>();var lookup=new Dictionary<Vec3,int>();
        int Index(Vec3 p) { if(lookup.TryGetValue(p,out var i)) return i; i=vertices.Count;lookup.Add(p,i);vertices.Add(p);return i; }
        Faces=new RenderFace[triangles.Length];
        for(var i=0;i<triangles.Length;i++)
        {
            if((i&1023)==0) token.ThrowIfCancellationRequested();
            var t=triangles[i];var normal=Vec3.Cross(t.B-t.A,t.C-t.A);var length=normal.Length;
            Faces[i]=new(Index(t.A),Index(t.B),Index(t.C),length>1e-14?normal*(1/length):default,t.Material);
        }
        Vertices=vertices.ToArray();
    }

    public static PreviewScene Create(Mesh3D mesh, UpAxis up = UpAxis.Z, CancellationToken token = default)
    {
        if (mesh.Triangles.Length == 0) throw new InvalidDataException("Модель не содержит поверхностей.");
        Vec3 Convert(Vec3 v) => up == UpAxis.Z ? new(v.X,v.Z,-v.Y) : v;
        var min = new Vec3(double.MaxValue,double.MaxValue,double.MaxValue);
        var max = new Vec3(double.MinValue,double.MinValue,double.MinValue);
        void Visit(Vec3 raw)
        {
            if (!raw.IsValid) throw new InvalidDataException("В модели есть недопустимые координаты.");
            var v=Convert(raw);
            min=new(Math.Min(min.X,v.X),Math.Min(min.Y,v.Y),Math.Min(min.Z,v.Z));
            max=new(Math.Max(max.X,v.X),Math.Max(max.Y,v.Y),Math.Max(max.Z,v.Z));
        }
        foreach (var t in mesh.Triangles) { token.ThrowIfCancellationRequested(); Visit(t.A);Visit(t.B);Visit(t.C); }
        var span=Math.Max(max.X-min.X,Math.Max(max.Y-min.Y,max.Z-min.Z));
        if (!(span > 0) || !double.IsFinite(span)) throw new InvalidDataException("Модель имеет нулевой или некорректный размер.");
        var origin=new Vec3((min.X+max.X)/2,min.Y,(min.Z+max.Z)/2);
        // Divide before multiplying: small/large source units must not affect camera fitting.
        Vec3 Normalize(Vec3 v)
        {
            var p=Convert(v)-origin;
            return new(p.X/span*2,p.Y/span*2,p.Z/span*2);
        }
        var triangles=new List<Triangle3>(mesh.Triangles.Length+384);
        double horizontal=0,height=0;
        void Bounds(Vec3 v) { horizontal=Math.Max(horizontal,Math.Sqrt(v.X*v.X+v.Z*v.Z));height=Math.Max(height,v.Y); }
        foreach (var t in mesh.Triangles)
        {
            token.ThrowIfCancellationRequested();
            var a=Normalize(t.A);var b=Normalize(t.B);var c=Normalize(t.C);
            Bounds(a);Bounds(b);Bounds(c);
            if (Vec3.Cross(b-a,c-a).Length > 1e-14) triangles.Add(new(a,b,c));
        }
        if (triangles.Count==0) throw new InvalidDataException("Поверхности модели вырождены: нечего показывать.");
        var modelCount=triangles.Count;
        var radius=Math.Max(.68,horizontal*1.12);
        const double baseY=-.16;
        for (var i=0;i<64;i++)
        {
            var a=i*Math.Tau/64;var b=(i+1)*Math.Tau/64;
            Vec3 Ring(double angle,double y,double r) => new(Math.Cos(angle)*r,y,Math.Sin(angle)*r);
            var p=Ring(a,0,radius);var q=Ring(b,0,radius);
            var r=Ring(a,baseY,radius);var s=Ring(b,baseY,radius);
            triangles.Add(new(new Vec3(0,0,0),q,p,1));
            triangles.Add(new(p,q,s,2));triangles.Add(new(p,s,r,2));
            var inner=radius*.965;
            var u=Ring(a,.004,inner);var v=Ring(b,.004,inner);
            triangles.Add(new(p,q,v,3));triangles.Add(new(p,v,u,3));
        }
        var center=(height+baseY)/2;
        // Rotationally invariant bounding sphere includes the model AND its pedestal.
        var sphere=Math.Sqrt(radius*radius+Math.Pow((height-baseY)/2,2));
        return new(triangles.ToArray(),center,sphere,modelCount,token);
    }

    public static Mesh3D Demo()
    {
        var triangles=new List<Triangle3>();
        void Crystal(double x,double y,double z,double radius,double height)
        {
            const int sides=7;
            var tip=new Vec3(x,y,z+height);
            var baseCenter=new Vec3(x,y,z);
            for(var i=0;i<sides;i++)
            {
                var a=i*Math.Tau/sides;var b=(i+1)*Math.Tau/sides;
                Vec3 Ring(double angle,double level,double r) => new(x+Math.Cos(angle)*r,y+Math.Sin(angle)*r,z+level);
                var p=Ring(a,0,radius);var q=Ring(b,0,radius);
                var r=Ring(a,height*.68,radius*.82);var s=Ring(b,height*.68,radius*.82);
                triangles.Add(new(p,q,s));triangles.Add(new(p,s,r));triangles.Add(new(r,s,tip));triangles.Add(new(q,p,baseCenter));
            }
        }
        Crystal(0,0,0,.43,2.15);Crystal(.58,.06,0,.25,1.08);Crystal(-.45,-.21,0,.23,.75);
        return new(triangles.ToArray());
    }
}
