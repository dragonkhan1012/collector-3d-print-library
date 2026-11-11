using System.Globalization;
using System.Text;
using Collector.Core.Preview;

var root=Path.Combine(Path.GetTempPath(),"collector-3d-"+Guid.NewGuid());Directory.CreateDirectory(root);
var checks=0;
void Check(bool condition,string message) { if(!condition) throw new Exception(message);checks++;Console.WriteLine("PASS: "+message); }
void Invalid(Action action,string message)
{
    var rejected=false;try { action(); } catch(InvalidDataException) { rejected=true; }Check(rejected,message);
}
Mesh3D Box(double x,double y,double z,Vec3 offset=default)
{
    Vec3[] v=[new(0,0,0),new(x,0,0),new(x,y,0),new(0,y,0),new(0,0,z),new(x,0,z),new(x,y,z),new(0,y,z)];
    int[] indices=[0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,1,2,6,1,6,5,2,3,7,2,7,6,3,0,4,3,4,7];
    return new(Enumerable.Range(0,indices.Length/3).Select(i=>new Triangle3(v[indices[i*3]]+offset,v[indices[i*3+1]]+offset,v[indices[i*3+2]]+offset)).ToArray());
}
try
{
    var ascii=Path.Combine(root,"sample.stl");
    File.WriteAllText(ascii,"solid triangle\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1.5 0 0\nvertex 0 2 0\nendloop\nendfacet\nendsolid triangle\n");
    CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("ru-RU");
    Check(MeshReader.Read(ascii).Triangles.Single().B.X==1.5,"ASCII STL и дроби не зависят от языка ОС");
    var binary=Path.Combine(root,"binary.stl");
    using(var writer=new BinaryWriter(File.Create(binary)))
    {
        var header=new byte[80];Encoding.ASCII.GetBytes("solid but binary").CopyTo(header,0);writer.Write(header);writer.Write(1u);
        foreach(var x in new float[] {0,0,1,0,0,0,1,0,0,0,1,0}) writer.Write(x);writer.Write((ushort)0);
    }
    Check(MeshReader.Read(binary).Triangles.Length==1,"бинарный STL с заголовком solid распознаётся правильно");
    var obj=Path.Combine(root,"sample.obj");
    File.WriteAllText(obj,"v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nf -4/1/1 -3/2/1 -2/3/1 -1/4/1 # quad\nmtllib external-secret-file.mtl\n");
    Check(MeshReader.Read(obj).Triangles.Length==2,"OBJ: многоугольники, отрицательные индексы, комментарии; внешние материалы не читаются");
    File.WriteAllText(obj,"v 0 0 0\nf 1 2 3\n");Invalid(()=>MeshReader.Read(obj),"OBJ с неверными индексами отвергается");
    File.WriteAllText(obj,"v NaN 0 0\n");Invalid(()=>MeshReader.Read(obj),"NaN не попадает в рендерер");
    File.WriteAllText(ascii,"solid broken\nfacet normal 0 0 1\nvertex 0 0 0\n");Invalid(()=>MeshReader.Read(ascii),"обрезанный STL отвергается");
    using(var file=File.Create(binary)) file.SetLength(128);
    Invalid(()=>MeshReader.Read(binary),"повреждённый бинарный файл не принимается за модель");
    using(var file=File.Create(binary)) file.SetLength(MeshReader.MaximumBytes+1);
    Invalid(()=>MeshReader.Read(binary),"слишком большой файл останавливается до чтения в память");
    using(var writer=new BinaryWriter(File.Create(binary)))
    {
        writer.Write(new byte[80]);writer.Write((uint)MeshReader.MaximumTriangles+1);
        writer.BaseStream.SetLength(84L+50L*(MeshReader.MaximumTriangles+1));
    }
    Invalid(()=>MeshReader.Read(binary),"слишком много треугольников останавливается до выделения памяти");
    using(var cancellation=new CancellationTokenSource())
    {
        cancellation.Cancel();var cancelled=false;try { MeshReader.Read(ascii,cancellation.Token); }catch(OperationCanceledException) { cancelled=true; }
        Check(cancelled,"загрузка отменяется при закрытии или переключении файла");
    }
    Invalid(()=>PreviewScene.Create(new Mesh3D([new(default,default,default)])),"нулевая модель отвергается");
    Invalid(()=>PreviewScene.Create(new Mesh3D([new(new(double.PositiveInfinity,0,0),new(1,0,0),new(0,1,0))])),"бесконечные координаты отвергаются");
    Mesh3D[] shapes=[Box(1,1,1),Box(.01,.01,500),Box(1000,2,.01),Box(.0001,1000,.01),Box(2,2,0),Box(1e-30,2e-30,3e-30),Box(1e30,2e30,3e30),Box(3,1,7,new(1e10,-1e10,1e10)),PreviewScene.Demo()];
    var projected=0;
    foreach(var shape in shapes)
        foreach(var axis in Enum.GetValues<UpAxis>())
        {
            var scene=PreviewScene.Create(shape,axis);
            foreach(var (width,height) in new[] {(64,64),(320,320),(180,560),(560,180),(800,800)})
                foreach(var elevation in new[] {.05,.32,1.3})
                    for(var step=0;step<24;step++)
                        foreach(var t in scene.Triangles)
                            foreach(var v in new[] {t.A,t.B,t.C})
                            {
                                var p=PreviewRenderer.Project(v,scene,width,height,step*Math.Tau/24,elevation);
                                if(!double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X<0 || p.Y<0 || p.X>width || p.Y>height)
                                    throw new Exception($"Обрезана модель: {width}x{height}, axis {axis}, elevation {elevation}, point {p}");
                                projected++;
                            }
        }
    Check(projected>1_000_000,$"модель и пьедестал вписаны: {projected:N0} проекций, 9 форм, обе оси, 24 угла, узкие/широкие/квадратные окна");
    var demo=PreviewScene.Create(PreviewScene.Demo());
    var a=PreviewRenderer.Render(demo,200,0,.3);var b=PreviewRenderer.Render(demo,200,1,.3);
    Check(!a.SequenceEqual(b),"поворот меняет реальную 3D-проекцию");
    Check(a.All(p=>(uint)p>>24==255),"все пиксели кадра непрозрачны");
    var reversed=PreviewScene.Create(new Mesh3D(PreviewScene.Demo().Triangles.Reverse().ToArray()));
    Check(a.SequenceEqual(PreviewRenderer.Render(reversed,200,0,.3)),"буфер глубины не зависит от порядка граней");
    using(var cancellation=new CancellationTokenSource())
    {
        cancellation.Cancel();var cancelled=false;try { PreviewRenderer.Render(demo,128,0,.3,1,cancellation.Token); }catch(OperationCanceledException) { cancelled=true; }
        Check(cancelled,"рендеринг отменяется без зависания");
    }
    var dense = new List<Triangle3>();
    Vec3 Torus(double u, double v) => new((1+.3*Math.Cos(v))*Math.Cos(u),(1+.3*Math.Cos(v))*Math.Sin(u),.4+.3*Math.Sin(v));
    for (var u=0;u<128;u++) for (var v=0;v<64;v++)
    {
        var tA=Torus(u*Math.Tau/128,v*Math.Tau/64);var tB=Torus((u+1)*Math.Tau/128,v*Math.Tau/64);
        var tC=Torus((u+1)*Math.Tau/128,(v+1)*Math.Tau/64);var tD=Torus(u*Math.Tau/128,(v+1)*Math.Tau/64);
        dense.Add(new(tA,tB,tC));dense.Add(new(tA,tC,tD));
    }
    var denseFile=Path.Combine(root,"dense.stl");
    using(var writer=new BinaryWriter(File.Create(denseFile)))
    {
        writer.Write(new byte[80]);writer.Write((uint)dense.Count);
        foreach(var t in dense)
        {
            writer.Write(0f);writer.Write(0f);writer.Write(0f);
            foreach(var p in new[]{t.A,t.B,t.C}) { writer.Write((float)p.X);writer.Write((float)p.Y);writer.Write((float)p.Z); }
            writer.Write((ushort)0);
        }
    }
    var watch=System.Diagnostics.Stopwatch.StartNew();
    var loaded=MeshReader.Read(denseFile);var sceneDense=PreviewScene.Create(loaded);
    var densePixels=PreviewRenderer.Render(sceneDense,320,.4,.35);
    Check(loaded.Triangles.Length==16384 && densePixels.Length==320*320,
        $"реальный бинарный STL: 16 384 грани, загрузка и кадр {watch.ElapsedMilliseconds} мс");
    var reusable=new PreviewRenderBuffer();
    var cached=PreviewRenderer.Render(sceneDense,reusable,320,.4,.35).ToArray();
    Check(cached.SequenceEqual(PreviewRenderer.Render(sceneDense,320,.4,.35)),"повторно используемый буфер даёт тот же кадр");
    PreviewRenderer.Render(sceneDense,reusable,128,1,.5);
    Check(cached.SequenceEqual(PreviewRenderer.Render(sceneDense,reusable,320,.4,.35)),"изменение размера не оставляет старые пиксели и глубину");
    PreviewRenderer.Render(demo,reusable,320,.4,.35);
    Check(cached.SequenceEqual(PreviewRenderer.Render(sceneDense,reusable,320,.4,.35)),"замена сцены не оставляет старые вершины");
    using(var cancelled=new CancellationTokenSource())
    {
        cancelled.Cancel();try { PreviewRenderer.Render(sceneDense,reusable,320,1,.5,1,cancelled.Token); }catch(OperationCanceledException) { }
        Check(cached.SequenceEqual(PreviewRenderer.Render(sceneDense,reusable,320,.4,.35)),"отмена освобождает буфер для следующего кадра");
    }
    var allocated=GC.GetAllocatedBytesForCurrentThread();
    for(var i=0;i<5;i++) PreviewRenderer.Render(sceneDense,reusable,320,i*.1,.35);
    Check(GC.GetAllocatedBytesForCurrentThread()-allocated<128_000,"серия кадров не выделяет новые массивы пикселей, глубины и вершин");
    Console.WriteLine($"Итого: {checks} проверок 3D.");
}
finally { Directory.Delete(root,true); }
