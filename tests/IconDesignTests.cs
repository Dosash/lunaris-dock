using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lunaris;

internal static class IconDesignTests
{
    const int Side = 96;
    const BindingFlags InstanceMethod = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static void Require(bool value, string label)
    {
        if (!value) throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }

    static byte[] Pixels(IconDesign design)
    {
        var view = (UIElement)typeof(IconDesign).GetMethod("Render", InstanceMethod).Invoke(design, new object[]{ (double)Side });
        view.Measure(new Size(Side,Side));
        view.Arrange(new Rect(0,0,Side,Side));
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Side,Side,96,96,PixelFormats.Pbgra32);
        bitmap.Render(view);
        var result = new byte[Side * Side * 4];
        bitmap.CopyPixels(result, Side * 4, 0);
        return result;
    }

    static void Test()
    {
        var settings = new UserSettings();
        var library = new LaunchLibrary(settings,new AppEntry[0]);
        string id = library.CreateFolder("Art",LaunchLibrary.Root);
        var design = new IconDesign { Key=id, Shape="folder", BaseColor="#C82542", InkColor="#FFFFFF",
            Foreground="symbol", Glyph="star", Size=50 };
        var original = Pixels(design);
        Require(original.Where((value,index) => index % 4 == 3 && value > 200).Count() > 1000,
            "custom folder artwork produces a visible high-resolution image");
        design.Glyph="heart";
        Require(!Pixels(design).SequenceEqual(original), "choosing another bundled glyph changes the rendered image");
        design.Glyph="star"; design.OffsetX=10; design.Size=70;
        Require(!Pixels(design).SequenceEqual(original), "the foreground placement and size alter the composition");
        design.Foreground="text"; design.Text="QA";
        var lettering=Pixels(design);
        Require(!lettering.SequenceEqual(original), "text can replace the symbol");
        settings.IconDesigns.Add(design);
        library.RenameFolder(id,"Renamed");
        SettingsStore.Save(settings);
        var restored=SettingsStore.Load();
        var restoredArt=restored.IconDesigns.Single(item=>item.Key==id);
        Require(restored.Library.Folders.Single().Name=="Renamed" && Pixels(restoredArt).SequenceEqual(lettering),
            "renamed folders retain their exact saved artwork across profile reload");
    }

    static void TestImports(string profile)
    {
        const int width=64, height=64;
        byte[] rgba=new byte[width*height*4];
        for(int i=0;i<rgba.Length;i+=4) {
            rgba[i+1]=200; rgba[i+2]=60; rgba[i+3]=255;
        }
        var source=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,rgba,width*4);
        var encoder=new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        string png=Path.Combine(profile,"art.png"), ico=Path.Combine(profile,"art.ico");
        using(var stream=File.Create(png)) encoder.Save(stream);
        byte[] payload=File.ReadAllBytes(png);
        using(var writer=new BinaryWriter(File.Create(ico))) {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
            writer.Write((byte)width); writer.Write((byte)height); writer.Write((ushort)0);
            writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(payload.Length); writer.Write(22); writer.Write(payload);
        }
        var load=typeof(IconDesign).GetMethod("ImportImage",BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Static);
        foreach(var file in new[]{ png, ico }) {
            string embedded=(string)load.Invoke(null,new object[]{file});
            File.Delete(file);
            var settings=new UserSettings();
            settings.IconDesigns.Add(new IconDesign { Key=LaunchLibrary.AppKey("paint"), Shape="none", Foreground="image",
                Size=70, BaseColor="#000000", InkColor="#FFFFFF", ImagePng=embedded });
            SettingsStore.Save(settings);
            var migrated=SettingsStore.Load().IconDesigns.Single();
            var pixels=Pixels(migrated);
            int green=Enumerable.Range(0,Side*Side).Count(i=>pixels[i*4+1]>180 && pixels[i*4+2]<80 && pixels[i*4+3]==255);
            Require(green>300, "imported "+Path.GetExtension(file)+" is embedded and renders after the original file is removed");
        }
    }

    [STAThread]
    public static int Main()
    {
        var profile = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"Lunaris-IconTests-"+Guid.NewGuid().ToString("N")));
        try {
            Environment.SetEnvironmentVariable("LUNARIS_PROFILE",profile.FullName);
            Test(); TestImports(profile.FullName); return 0;
        }
        catch(Exception error) { Console.WriteLine(error); return 1; }
        finally { profile.Delete(true); }
    }
}
