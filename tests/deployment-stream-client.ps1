param([string]$Root=(Split-Path -Parent $PSScriptRoot),[int]$Port,[string]$Fingerprint,[string]$Scenario='stream')
$ErrorActionPreference='Stop'
foreach($name in 'Newtonsoft.Json','PCLCS','Renci.SshNet','BouncyCastle.Cryptography') { [Reflection.Assembly]::LoadFrom((Join-Path $Root "PCLCS/bin/$name.dll")) | Out-Null }
Add-Type -ReferencedAssemblies @((Join-Path $Root 'PCLCS/bin/PCLCS.dll'),(Join-Path $Root 'PCLCS/bin/Newtonsoft.Json.dll')) -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Threading;
using Newtonsoft.Json.Linq;
using PCLCS.Server;
public static class CptStreamTest {
    public static readonly ConcurrentQueue<DeploymentUpdate> Events = new ConcurrentQueue<DeploymentUpdate>();
    public static void Run(int port,string fingerprint,string scenario) {
        var profile = new ServerProfile { Host="127.0.0.1", SshPort=port, User="root", Fingerprint=fingerprint, Root="/fixture/server" };
        var finished = false; var live=false;var pauseSeen=false;var resumed=false;
        var control=new DeploymentControl();
        if(scenario=="resume") {
            var requestResume=new JObject();requestResume["action"]="deploy-resume";
            var installed=false;
            var restored=ServerManager.DeployAndStart(profile,"fixture-ssh-not-real",requestResume,line=>{},installed:data=>{installed=true;});
            if(!installed || !profile.Deployed || restored["client"]==null) throw new Exception("Installed continuation did not finish the startup/client stages.");
            Console.WriteLine("Installed continuation skipped installation and duplicate startup, retained installed state and generated client configuration.");
            return;
        }
        var request=new JObject(); request["action"]="client-pack";
        var result=ServerManager.Run(profile,"fixture-ssh-not-real",request,update: e => {
            Events.Enqueue(e);
            if(e.Kind=="download" && !finished) { live=true;control.Pause(); }
            if(e.Kind=="paused") { pauseSeen=control.IsPaused;Thread.Sleep(250);control.Resume(); }
            if(e.Kind=="resumed") resumed=true;
        },control:control);
        finished=true;
        if(!live || !pauseSeen || !resumed || Events.Count<5 || result["message"].ToString()!="fixture complete") throw new Exception("Live progress/pause/resume did not complete.");
        Console.WriteLine("SFTP live progress, remote pause acknowledgment, resume and isolated final JSON passed.");
    }
}
'@
[CptStreamTest]::Run($Port,$Fingerprint,$Scenario)
