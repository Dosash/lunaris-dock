using System;
using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Xml;
using Lunaris;

// Exercise the same decision and native parameter construction used immediately
// before WlanConnect/WlanDisconnect. No WLAN service, radio or saved profile needed.
internal static class WirelessParameterTests
{
    const BindingFlags Fields=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static readonly Type Wifi=typeof(UserSettings).Assembly.GetType("Lunaris.WifiNetwork",true);
    static readonly MethodInfo Build=Wifi.Assembly.GetType("Lunaris.WirelessNetworks",true)
        .GetMethod("BuildRequest",BindingFlags.Static|BindingFlags.NonPublic);
    static readonly Guid Adapter=Guid.NewGuid();
    // Arbitrary SSID octets, including '<', '&', NUL and a non-UTF8 byte.
    const string Hex="513C2600FF";
    static object Network(string auth,string encryption,string profile=null,bool active=false)
    {
        var entry=Activator.CreateInstance(Wifi,true);
        Put(entry,"Adapter",Adapter); Put(entry,"Hex",Hex); Put(entry,"Name","QA <&>");
        Put(entry,"Authentication",auth); Put(entry,"Encryption",encryption);
        Put(entry,"Profile",profile); Put(entry,"Connectable",true); Put(entry,"Connected",active);
        return entry;
    }
    static void Put(object obj,string name,object value) { obj.GetType().GetField(name,Fields).SetValue(obj,value); }
    static object Field(object obj,string name) { return obj.GetType().GetField(name,Fields).GetValue(obj); }
    static object Request(object selected,bool disconnect,string key,params object[] snapshot)
    {
        var list=(IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(Wifi));
        foreach(var entry in snapshot) list.Add(entry);
        try { return Build.Invoke(null,new object[]{selected,list,disconnect,key}); }
        catch(TargetInvocationException ex) { throw ex.InnerException; }
    }
    static void Require(bool value,string text) { if(!value) throw new Exception(text); }
    static void Refused(Action action,int code,string label)
    {
        try { action(); }
        catch(Win32Exception ex) {
            if(ex.NativeErrorCode!=code) throw;
            Console.WriteLine("PASS "+label); return;
        }
        throw new Exception(label);
    }
    static object Expect(object request,string kind)
    {
        Require(Field(request,"Kind").ToString()==kind,"Expected "+kind+" WLAN operation");
        Require((Guid)Field(request,"Adapter")==Adapter && (string)Field(request,"SsidHex")==Hex,
            "WLAN operation targets the selected adapter and exact SSID.");
        return Field(request,"Parameters");
    }
    static string XmlValue(XmlDocument doc,XmlNamespaceManager ns,string path)
    {
        var node=doc.SelectSingleNode("/w:WLANProfile/"+path,ns);
        return node==null ? null : node.InnerText;
    }
    static void Temporary(string auth,string encryption,string key,string expectedKeyType)
    {
        var entry=Network(auth,encryption);
        var parameters=Expect(Request(entry,false,key,entry),"Connect");
        Require((uint)Field(parameters,"Mode")==1 && (uint)Field(parameters,"BssType")==1,
            "Temporary WLAN XML must target an infrastructure network.");
        var doc=new XmlDocument { XmlResolver=null };
        doc.LoadXml((string)Field(parameters,"Profile"));
        const string v1="http://www.microsoft.com/networking/WLAN/profile/v1";
        var ns=new XmlNamespaceManager(doc.NameTable); ns.AddNamespace("w",v1);
        Require(doc.DocumentElement.NamespaceURI==v1 && XmlValue(doc,ns,"w:SSIDConfig/w:SSID/w:hex")==Hex &&
            XmlValue(doc,ns,"w:connectionMode")=="manual" && XmlValue(doc,ns,"w:connectionType")=="ESS",
            "Temporary profile lost the selected SSID or connection policy.");
        string security="w:MSM/w:security/";
        Require(XmlValue(doc,ns,security+"w:authEncryption/w:authentication")==auth &&
            XmlValue(doc,ns,security+"w:authEncryption/w:encryption")==encryption,"Wrong authentication or cipher.");
        if(expectedKeyType==null) Require(XmlValue(doc,ns,security+"w:sharedKey")==null,"Unexpected key in an open/OWE profile.");
        else Require(XmlValue(doc,ns,security+"w:sharedKey/w:keyType")==expectedKeyType &&
            XmlValue(doc,ns,security+"w:sharedKey/w:keyMaterial")==key &&
            XmlValue(doc,ns,security+"w:sharedKey/w:protected")=="false","Credential XML did not round-trip.");
        Console.WriteLine("PASS "+auth+" profile retains exact SSID octets, authentication, cipher and credential semantics");
    }
    public static int Main()
    {
        try {
            const string profile="Saved <QA> & Ω";
            var saved=Network(null,null,profile);
            var parameters=Expect(Request(saved,false,null,saved),"Connect");
            Require((uint)Field(parameters,"Mode")==0 && (uint)Field(parameters,"BssType")==1 &&
                (string)Field(parameters,"Profile")==profile,"A saved profile must pass unchanged by name.");
            Console.WriteLine("PASS a saved network uses its literal Windows profile name and profile mode");
            Temporary("open","none",null,null); Temporary("OWE","AES",null,null);
            Temporary("WPA2PSK","AES","pass<&>'\"word","passPhrase");
            Temporary("WPA2PSK","AES",new string('A',64),"networkKey");
            Temporary("WPA3SAE","AES","new<&>key","passPhrase");
            var password=Network("WPA2PSK","AES");
            Refused(()=>Request(password,false,"short",password),86,"invalid credentials are rejected before a native call");
            var enterprise=Network(null,null);
            Refused(()=>Request(enterprise,false,null,enterprise),50,"an enterprise network without a saved profile is rejected");
            var active=Network(null,null,profile,true);
            Expect(Request(saved,false,null,active),"NoChange");
            Expect(Request(saved,true,null,active),"Disconnect");
            Console.WriteLine("PASS a current selection is a no-op for connect and dispatches disconnect for its adapter");
            var other=Network(null,null,profile,true); Put(other,"Hex","CAFEBABE");
            Refused(()=>Request(active,true,null,saved,other),1168,"a stale selection cannot disconnect the newly active SSID");
            Refused(()=>Request(active,true,null),1168,"a vanished selection cannot disconnect its old adapter");
            var wrongAdapter=Network(null,null,profile,true); Put(wrongAdapter,"Adapter",Guid.NewGuid());
            Refused(()=>Request(active,true,null,wrongAdapter),1168,"the same SSID on a different adapter cannot be disconnected");
            var differentProfile=Network(null,null,profile+"2",true);
            Refused(()=>Request(active,true,null,differentProfile),1168,"a changed Windows profile cannot be disconnected by an old row");
            return 0;
        } catch(Exception ex) { Console.WriteLine("FAIL "+ex); return 1; }
    }
}
