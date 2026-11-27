using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Lunaris;

internal static class DockArtMotionTests
{
    static readonly Assembly Product=typeof(UserSettings).Assembly;
    const BindingFlags HiddenStatic=BindingFlags.NonPublic|BindingFlags.Static;
    static void Check(bool passed,string caption)
    {
        if(!passed) throw new Exception(caption);
        Console.WriteLine("PASS "+caption);
    }
    static bool Near(double a,double b) { return Math.Abs(a-b)<.00001; }
    static void Motion()
    {
        var type=Product.GetType("Lunaris.DockSurface+SlideCurve",true);
        var factory=type.GetMethod("Create",HiddenStatic);
        var speed=type.GetMethod("Speed",BindingFlags.NonPublic|BindingFlags.Instance);
        var duration=type.GetField("Millis",BindingFlags.NonPublic|BindingFlags.Instance);
        Func<double,double,double,IEasingFunction> make=(a,b,v)=>(IEasingFunction)factory.Invoke(null,new object[]{a,b,v,80.0,true});
        var down=make(0,80,0);
        double last=0;
        for(int i=1;i<=100;i++) {
            double next=down.Ease(i/100.0);
            if(next<=last || next>1) throw new Exception("Uninterrupted hide jumped or left its endpoints.");
            last=next;
        }
        Check(Near(down.Ease(0),0) && Near(down.Ease(1),1) && down.Ease(.1)<.05 && down.Ease(.9)>.95,
            "Dock starts and finishes gently and advances monotonically through all samples");
        double u=.44, from=80*down.Ease(u), inherited=(double)speed.Invoke(down,new object[]{u});
        var back=make(from,0,inherited);
        Func<double,double> y=t=>from*(1-back.Ease(t));
        double step=.0001;
        double observed=(y(step)-y(0))/(step*(double)duration.GetValue(back));
        Check(Near(y(0),from) && Math.Abs(observed-inherited)<.002 && y(.05)>from && Near(y(1),0),
            "mid-flight reversal carries measured velocity forward, brakes, then returns to the same monitor edge");
        Check(Enumerable.Range(0,101).All(i=>y(i/100.0)>=0 && y(i/100.0)<=80),
            "reversed Dock stays inside its own 80-DIP travel");
        var animation=new DoubleAnimation(0,80,TimeSpan.FromMilliseconds((double)duration.GetValue(down))) { EasingFunction=down };
        var clock=animation.CreateClock();
        var transform=new TranslateTransform();
        transform.ApplyAnimationClock(TranslateTransform.YProperty,clock);
        clock.Controller.Begin();
        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromTicks(animation.Duration.TimeSpan.Ticks/2),TimeSeekOrigin.BeginTime);
        Check(Math.Abs(transform.Y-40)<.1,"WPF clock half-travel transform "+transform.Y.ToString("F2")+" DIP");
        transform.ApplyAnimationClock(TranslateTransform.YProperty,null);
        Check(factory.Invoke(null,new object[]{60.0,0.0,.4,80.0,false})==null &&
              factory.Invoke(null,new object[]{.1,0.0,0.0,80.0,true})==null,
              "reduced motion and subpixel distances complete immediately without an animation clock");
    }

    static PixelFormat Normalize(byte[] pixels)
    {
        return (PixelFormat)Product.GetType("Lunaris.ShellCatalog",true).GetMethod("NormalizeShellPixels",HiddenStatic)
            .Invoke(null,new object[]{pixels});
    }
    static void Alpha()
    {
        byte[] straight={200,128,255,64, 15,20,30,0, 255,255,255,255, 224,3,192,255};
        Check(Normalize(straight)==PixelFormats.Pbgra32 &&
              straight.SequenceEqual(new byte[]{50,32,64,64, 0,0,0,0, 255,255,255,255, 224,3,192,255}),
              "semi-transparent shell colour channels premultiply once; hidden pixels, opaque white and brand colours are correct");
        byte[] premult={14,54,64,80, 0,0,0,0, 180,90,17,255};
        byte[] unchanged=(byte[])premult.Clone();
        Check(Normalize(premult)==PixelFormats.Pbgra32 && premult.SequenceEqual(unchanged),
              "already premultiplied artwork keeps exact samples");
        byte[] rgb={255,255,255,0, 2,4,8,0};
        Check(Normalize(rgb)==PixelFormats.Bgr32 && rgb[0]==255 && rgb[6]==8,"legacy opaque RGB shell art retains its colour");
        using(var platform=new PlatformServices(true))
        {
            int bitmaps=0,partial=0;
            foreach(var app in platform.GetApplications())
            {
                var b=app.Icon as BitmapSource;
                if(b==null || b.Format!=PixelFormats.Pbgra32) continue;
                byte[] bytes=new byte[b.PixelWidth*b.PixelHeight*4];
                b.CopyPixels(bytes,b.PixelWidth*4,0);
                for(int i=0;i<bytes.Length;i+=4) {
                    int a=bytes[i+3];
                    if(bytes[i]>a || bytes[i+1]>a || bytes[i+2]>a) throw new Exception("Invalid live premultiplied sample: "+app.Name);
                    if(a!=0 && a!=255) partial++;
                }
                bitmaps++;
            }
            Check(bitmaps>0 && partial>0,"real Windows shell assets satisfy premultiplied alpha ("+bitmaps+" icons, "+partial+" translucent pixels)");
        }
    }

    static BitmapSource Artwork(Color c,bool full)
    {
        const int side=32;
        var pixels=new byte[side*side*4];
        for(int y=0;y<side;y++) for(int x=0;x<side;x++) {
            if(!full && (x<7 || x>24 || y<7 || y>24)) continue;
            int i=(y*side+x)*4;
            pixels[i]=c.B; pixels[i+1]=c.G; pixels[i+2]=c.R; pixels[i+3]=255;
        }
        var source=BitmapSource.Create(side,side,96,96,PixelFormats.Bgra32,null,pixels,side*4);
        source.Freeze(); return source;
    }
    static Color Pixel(byte[] pixels,int x,int y)
    {
        int i=(y*64+x)*4;
        return Color.FromArgb(pixels[i+3],pixels[i+2],pixels[i+1],pixels[i]);
    }
    static void Art()
    {
        var icon=Product.GetType("Lunaris.Visuals",true).GetMethod("Icon",HiddenStatic);
        var refresh=Product.GetType("Lunaris.Appearance",true).GetMethod("Refresh",HiddenStatic);
        var teal=Color.FromRgb(5,178,145);
        foreach(string theme in new[]{"light","dark"}) {
            refresh.Invoke(null,new object[]{theme});
            foreach(var color in new[]{Colors.Black,Colors.White,teal}) foreach(bool full in new[]{false,true})
            {
                var src=Artwork(color,full);
                var frame=(Border)icon.Invoke(null,new object[]{new AppEntry{Name="test",Icon=src},64.0});
                var image=frame.Child as Image;
                if(image==null || image.Source!=src || frame.BorderBrush==null || frame.BorderThickness.Left<=0)
                    throw new Exception("Artwork/frame lost in "+theme);
                frame.Measure(new Size(64,64)); frame.Arrange(new Rect(0,0,64,64)); frame.UpdateLayout();
                var bitmap=new RenderTargetBitmap(64,64,96,96,PixelFormats.Pbgra32);
                bitmap.Render(frame);
                var bytes=new byte[64*64*4]; bitmap.CopyPixels(bytes,64*4,0);
                if(Pixel(bytes,32,32)!=color || Pixel(bytes,0,0).A!=0) throw new Exception("Logo colour/corner was altered in "+theme);
                if(!full) {
                    Color backing=Pixel(bytes,10,32);
                    if(backing.A!=255 || (color==Colors.White && backing.R>125) || (color==Colors.Black && backing.R<135))
                        throw new Exception("Monochrome logo lacks a contrasting frame in "+theme);
                } else if(Pixel(bytes,7,32)!=color) throw new Exception("Opaque full-bleed art was cropped or recoloured");
            }
            Check(true,"offscreen WPF "+theme+" frames retain original white, black and colour pixels in transparent and opaque icons");
        }
    }
    [STAThread] static int Main()
    {
        try { Motion(); Alpha(); Art(); return 0; }
        catch(Exception ex) { Console.WriteLine("FAIL "+ex); return 1; }
    }
}
