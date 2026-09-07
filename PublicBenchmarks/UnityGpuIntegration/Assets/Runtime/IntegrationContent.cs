using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Summit.GpuSensorPipeline;

namespace Summit.PublicIntegration
{
    [Serializable] public sealed class ContentEvent
    {
        public string action,utc,file; public int frame,bundle; public double elapsedMilliseconds; public long bytes;
    }
    public sealed class IntegrationContent
    {
        sealed class Load
        {
            public int id;public bool canceled,ready,registered;public double started;
            public AssetBundleCreateRequest request;public AssetBundle bundle;
            public AssetBundleRequest dataRequest,textureRequest;public TextAsset data;public Texture2D texture;
            public AssetBundleUnloadOperation unload;
        }
        readonly List<Load> loads=new List<Load>();
        readonly string root;
        readonly Action<ContentEvent> record;
        public Texture CurrentTexture {get;private set;}
        public int Pending { get {int n=0;foreach(var l in loads)if((l.request!=null&&!l.ready)||l.unload!=null)n++;return n;} }
        public IntegrationContent(Action<ContentEvent> record)
        { this.record=record;root=Path.Combine(Application.streamingAssetsPath,"IntegrationContent");CurrentTexture=Texture2D.whiteTexture; }
        void Mark(string action,int frame,Load l)
        { record(new ContentEvent{action=action,frame=frame,bundle=l.id,file="content-"+l.id,utc=DateTime.UtcNow.ToString("O"),elapsedMilliseconds=(Time.realtimeSinceStartupAsDouble-l.started)*1000,bytes=new FileInfo(Path.Combine(root,"content-"+l.id)).Length}); }
        void Request(int id,int frame,bool cancel)
        {
            var l=new Load{id=id,started=Time.realtimeSinceStartupAsDouble,canceled=cancel};
            l.request=AssetBundle.LoadFromFileAsync(Path.Combine(root,"content-"+id));
            if(l.request==null)throw new Exception("Disk AssetBundle request failed");
            loads.Add(l);Mark("load-request",frame,l);
            if(cancel)Mark("logical-cancel-request",frame,l);
        }
        public void Poll(int frame)
        {
            foreach(var l in loads)
            {
                if(l.unload!=null&&l.unload.isDone){Mark("unload-complete",frame,l);l.unload=null;l.bundle=null;l.request=null;}
                if(l.request==null||l.ready)continue;
                if(!l.request.isDone)continue;
                if(l.bundle==null)
                {
                    l.bundle=l.request.assetBundle;if(l.bundle==null)throw new Exception("AssetBundle decode failed");
                    Mark("disk-load-complete",frame,l);
                    if(l.canceled){l.ready=true;l.unload=l.bundle.UnloadAsync(true);Mark("canceled-discard-unload",frame,l);continue;}
                    l.dataRequest=l.bundle.LoadAssetAsync<TextAsset>("assets/generated/content-"+l.id+".bytes");
                    l.textureRequest=l.bundle.LoadAssetAsync<Texture2D>("assets/generated/palette-"+l.id+".asset");
                }
                if(!l.dataRequest.isDone||!l.textureRequest.isDone)continue;
                l.data=l.dataRequest.asset as TextAsset;l.texture=l.textureRequest.asset as Texture2D;
                if(l.data==null||l.texture==null)throw new Exception("Actual bundle assets unavailable");
                l.ready=true;Mark("assets-ready",frame,l);
            }
        }
        Load Find(int id)
        {
            for(int i=loads.Count-1;i>=0;i--)if(loads[i].id==id&&!loads[i].canceled)return loads[i];
            throw new Exception("Unrequested content");
        }
        public int Advance(int frame,GpuSensorSample[] samples,uint[] active,uint seed)
        {
            Poll(frame);int changed=0;
            if(frame==16)Request(0,frame,true);
            if(frame==80)Request(0,frame,false);
            if(frame==192)Request(1,frame,false);
            if(frame==128||frame==240)
            {
                var l=Find(frame==128?0:1);
                if(!l.ready)throw new Exception("Frozen content registration deadline missed; retain failed run");
                byte[] bytes=l.data.bytes;
                if(bytes.Length!=IntegrationFixture.ContentSlots*16)throw new Exception("Content byte count mismatch");
                int begin=IntegrationFixture.Capacity-2*IntegrationFixture.ContentSlots+l.id*IntegrationFixture.ContentSlots;
                using(var reader=new BinaryReader(new MemoryStream(bytes)))for(int i=0;i<IntegrationFixture.ContentSlots;i++)
                {
                    var s=new GpuSensorSample(reader.ReadUInt32(),reader.ReadUInt32(),reader.ReadUInt32(),reader.ReadUInt32());
                    if(!s.Equals(IntegrationFixture.ContentSample(l.id,i)))throw new Exception("Content payload mismatch");
                    s.Payload^=seed;samples[begin+i]=s;active[begin+i]=1;
                }
                l.registered=true;CurrentTexture=l.texture;changed+=IntegrationFixture.ContentSlots;Mark("register",frame,l);
            }
            if(frame==224||frame==320)
            {
                var l=Find(frame==224?0:1);int begin=IntegrationFixture.Capacity-2*IntegrationFixture.ContentSlots+l.id*IntegrationFixture.ContentSlots;
                for(int i=0;i<IntegrationFixture.ContentSlots;i++)active[begin+i]=0;
                l.registered=false;CurrentTexture=Texture2D.whiteTexture;changed+=IntegrationFixture.ContentSlots;Mark("unregister",frame,l);
            }
            if(frame==256||frame==352)
            {
                var l=Find(frame==256?0:1);l.unload=l.bundle.UnloadAsync(true);Mark("unload-request",frame,l);
            }
            return changed;
        }
        public void BeginCleanup(int frame)
        {
            CurrentTexture=Texture2D.whiteTexture;
            foreach(var l in loads) if(l.bundle!=null&&l.unload==null){l.unload=l.bundle.UnloadAsync(true);Mark("cleanup-unload",frame,l);}
        }
        public void MarkRendered(int frame)
        {if(frame==128||frame==240)Mark("first-use-render-submitted",frame,Find(frame==128?0:1));}
    }
}
