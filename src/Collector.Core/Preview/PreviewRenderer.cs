namespace Collector.Core.Preview;

public readonly record struct ProjectedPoint(double X,double Y,double Depth);

// A viewport owns one buffer and uses it serially. Returned pixels remain valid until its next render.
public sealed class PreviewRenderBuffer
{
    internal int[] Pixels=[];
    internal double[] Depth=[];
    internal ProjectedPoint[] Points=[];
    internal int Busy;
    internal void Ensure(int size,int vertices)
    {
        if(Pixels.Length!=size*size) { Pixels=new int[size*size];Depth=new double[size*size]; }
        if(Points.Length!=vertices) Points=new ProjectedPoint[vertices];
    }
}
public static class PreviewRenderer
{
    public static ProjectedPoint Project(Vec3 point,PreviewScene scene,int width,int height,double yaw,double elevation,double zoom=1)
    {
        var x=point.X*Math.Cos(yaw)+point.Z*Math.Sin(yaw);
        var z=-point.X*Math.Sin(yaw)+point.Z*Math.Cos(yaw);
        var y=point.Y-scene.CenterY;
        var scale=Math.Min(width,height)*.42/scene.Radius*Math.Clamp(zoom,.35,1);
        return new(width*.5+x*scale,height*.5-(y*Math.Cos(elevation)-z*Math.Sin(elevation))*scale,
            y*Math.Sin(elevation)+z*Math.Cos(elevation));
    }
    public static int[] Render(PreviewScene scene,int size,double yaw,double elevation,double zoom=1,CancellationToken token=default)
        =>Render(scene,new PreviewRenderBuffer(),size,yaw,elevation,zoom,token);
    public static int[] Render(PreviewScene scene,PreviewRenderBuffer buffer,int size,double yaw,double elevation,double zoom=1,CancellationToken token=default)
    {
        if(Interlocked.Exchange(ref buffer.Busy,1)!=0) throw new InvalidOperationException("Буфер кадра уже используется.");
        try { return RenderFrame(scene,buffer,size,yaw,elevation,zoom,token); }
        finally { Volatile.Write(ref buffer.Busy,0); }
    }
    private static int[] RenderFrame(PreviewScene scene,PreviewRenderBuffer buffer,int size,double yaw,double elevation,double zoom,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();size=Math.Clamp(size,64,1024);
        buffer.Ensure(size,scene.Vertices.Length);var pixels=buffer.Pixels;var depth=buffer.Depth;var points=buffer.Points;
        Array.Fill(depth,double.NegativeInfinity);
        for(var y=0;y<size;y++)
            for(var x=0;x<size;x++)
            {
                var dx=(x-size*.5)/size;var dy=(y-size*.42)/size;
                var glow=Math.Max(0,1-Math.Sqrt(dx*dx+dy*dy)*1.6);
                pixels[y*size+x]=Color(14+(int)(12*glow),20+(int)(17*glow),30+(int)(22*glow));
            }
        var cos=Math.Cos(yaw);var sin=Math.Sin(yaw);var ce=Math.Cos(elevation);var se=Math.Sin(elevation);
        var scale=size*.42/scene.Radius*Math.Clamp(zoom,.35,1);
        for(var i=0;i<scene.Vertices.Length;i++)
        {
            if((i&1023)==0) token.ThrowIfCancellationRequested();
            var p=scene.Vertices[i];var x=p.X*cos+p.Z*sin;var z=-p.X*sin+p.Z*cos;var y=p.Y-scene.CenterY;
            points[i]=new(size*.5+x*scale,size*.5-(y*ce-z*se)*scale,y*se+z*ce);
        }
        var light=new Vec3(-.45,.75,1);light=light*(1/light.Length);
        var view=new Vec3(0,se,ce);var half=light+view;half=half*(1/half.Length);
        var index=0;
        foreach(var t in scene.Faces)
        {
            if((index++ & 255)==0) token.ThrowIfCancellationRequested();
            var a=points[t.A];var b=points[t.B];var c=points[t.C];
            var area=(b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
            if(Math.Abs(area)<1e-8) continue;
            // Bounding pixel centres avoids shading the numerous subpixel faces in dense STL files.
            var minX=Math.Max(0,(int)Math.Ceiling(Math.Min(a.X,Math.Min(b.X,c.X))-.5));
            var maxX=Math.Min(size-1,(int)Math.Floor(Math.Max(a.X,Math.Max(b.X,c.X))-.5));
            var minY=Math.Max(0,(int)Math.Ceiling(Math.Min(a.Y,Math.Min(b.Y,c.Y))-.5));
            var maxY=Math.Min(size-1,(int)Math.Floor(Math.Max(a.Y,Math.Max(b.Y,c.Y))-.5));
            if(minX>maxX || minY>maxY) continue;
            var inverse=1/area;
            var ux=(b.Y-c.Y)*inverse;var uy=(c.X-b.X)*inverse;
            var vx=(c.Y-a.Y)*inverse;var vy=(a.X-c.X)*inverse;
            var px=minX+.5;var py=minY+.5;
            var rowU=((b.X-px)*(c.Y-py)-(b.Y-py)*(c.X-px))*inverse;
            var rowV=((c.X-px)*(a.Y-py)-(c.Y-py)*(a.X-px))*inverse;
            var da=a.Depth-c.Depth;var db=b.Depth-c.Depth;
            var depthX=ux*da+vx*db;var depthY=uy*da+vy*db;
            var rowDepth=c.Depth+rowU*da+rowV*db;
            // Lighting is lazy: invisible/occluded faces need no normal rotation or specular power.
            int? color=null;
            int Shade()
            {
                var n=t.Normal;var normal=new Vec3(n.X*cos+n.Z*sin,n.Y,-n.X*sin+n.Z*cos);
                if(Vec3.Dot(normal,view)<0) normal=normal*-1;
                var diffuse=.30+.65*Math.Max(0,Vec3.Dot(normal,light));
                var shine=t.Material==0 ? .14*Math.Pow(Math.Max(0,Vec3.Dot(normal,half)),24) : 0;
                var (r,g,bl)=t.Material switch {1=>(53,65,83),2=>(33,43,60),3=>(170,143,86),_=>(151,195,189)};
                return Color((int)(r*diffuse+255*shine),(int)(g*diffuse+255*shine),(int)(bl*diffuse+255*shine));
            }
            for(var y=minY;y<=maxY;y++,rowU+=uy,rowV+=vy,rowDepth+=depthY)
            {
                var u=rowU;var v=rowV;var z=rowDepth;var offset=y*size+minX;
                for(var x=minX;x<=maxX;x++,u+=ux,v+=vx,z+=depthX,offset++)
                    if(u>=-1e-10 && v>=-1e-10 && u+v<=1+1e-10 && z>depth[offset])
                    { depth[offset]=z;pixels[offset]=color??=Shade(); }
            }
        }
        token.ThrowIfCancellationRequested();return pixels;
    }
    private static int Color(int r,int g,int b) => unchecked((int)0xff000000)|Math.Clamp(r,0,255)<<16|Math.Clamp(g,0,255)<<8|Math.Clamp(b,0,255);
}
