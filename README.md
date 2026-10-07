## 🇧🇷  Worker Control - Microservices 🇧🇷
 <b>Worker Control</b> is a windows service developed to monitore your microservices. You control the number of applications you need, check a possible crash and make a balance with a boost in determinated time of your choice.

## 🚧 Version 2.0 (.NET)

Worker Control is being rewritten in .NET and this branch (`main`) will become version 2.0.

The Delphi version (1.x) stays available and keeps working:

- Source code: branch [`delphi-v1`](https://github.com/MurilloLazzaretti/Worker-Control/tree/delphi-v1)
- Last Delphi tag: [`v1.1.1`](https://github.com/MurilloLazzaretti/Worker-Control/tree/v1.1.1)

## ⚠️ Warning

Worker Control is in a <b>Beta</b> version for now, if you have any issue, please tell us. 

## 💉 Dependency

Worker Control needs a [`ZapMQ server`](https://github.com/MurilloLazzaretti/ZapMQ) to work: it is through ZapMQ that it talks to the applications it controls. Install ZapMQ first.

## 🔨 Build

Open `WorkerControl.dproj` in Delphi and build it for Win64. The [`ZapMQ Delphi Wrapper`](https://github.com/MurilloLazzaretti/ZapMQ-Delphi-Wrapper) it depends on is fetched with [`Boss`](https://github.com/HashLoad/boss):

```
boss install
```

`Management Studio\ManagementStudio.dproj` is the desktop application used to edit the configuration and follow the workers. Build it the same way.

## ⚙️ Installation

Run the commands in a PowerShell window opened as administrator. The examples use the folder `C:\WorkerControl`.

1. Copy `WorkerControl.exe`, `ConfigWorkers.json` and, if you use it, `ManagementStudio.exe` to `C:\WorkerControl`. The three must stay in the same folder: the service reads `ConfigWorkers.json` from the folder of its executable, and Management Studio edits that same file.

2. Edit `ConfigWorkers.json` as described below.

3. Register the service and start it:

```powershell
& C:\WorkerControl\WorkerControl.exe /install
Start-Service WorkerControlService
```

The service is registered as `WorkerControlService`, shown as "WorkerControl" in the services list. It starts with Windows and runs under the Local System account, and so does every application it starts.

4. Check that it is running:

```powershell
Get-Service WorkerControlService
```

Within the time set in `MonitoringRate`, the applications of the enabled groups appear in the Task Manager.

## ⚡️ Configuration

The settings are in `ConfigWorkers.json`, in the same folder as `WorkerControl.exe`.

```json
{
    "ZapMQHost" : "localhost",
    "ZapMQPort" : 5679,
    "RateLoadConfig" : 180000,
    "WorkerGroups" : [
        {
            "Enabled" : false,
            "Name" : "My App",
            "ApplicationFullPath" : "...",
            "TotalWorkers" : 2,
            "MonitoringRate": 30000,
            "TimeoutKeepAlive" : 125000,
            "Debug" : true,
            "Boost" : {
                "Enabled" : false,
                "BoostWorkers" : 3,
                "StartTime" : "12:00:00",
                "EndTime" : "12:30:00"    
            } 
        },
        {
            "Enabled" : false,
            "Name" : "My App 2",
            "ApplicationFullPath" : "...",
            "TotalWorkers" : 2,
            "MonitoringRate": 30000,
            "TimeoutKeepAlive" : 125000,
            "Debug" : true,
            "Boost" : {
                "Enabled" : false,
                "BoostWorkers" : 3,
                "StartTime" : "12:00:00",
                "EndTime" : "12:30:00"    
            } 
        }
    ]
}
```
✏ _Tips_

When you change the ConfigWorkers.json you dont need to restart the service, just wait to it notice the change.

## 🍬 JSON Properties

| _Property_                        | _Value_         | _Description_                                 |  
| --------------------------------- | --------------- | --------------------------------------------- |
|  ZapMQHost                        | String          | Ip of ZapMQ Service                           |
|  ZapMQPort                        | Integer         | Port of ZapMQ Service                         |
|  RateLoadConfig                   | Integer         | Freq to reaload config file                   |
|  WorkerGroups.Enabled             | Boolean         | Enable / Disable WorkerGroup                  |
|  WorkerGroups.Name                | String          | Name of your WorkerGroup                      |
|  WorkerGroups.ApplicationFullPath | String          | Full path to your .exe file                   |
|  WorkerGroups.TotalWorkers        | Integer         | Number of instances to open                   |
|  WorkerGroups.MonitoringRate      | Integer         | Miliseconds to check crashed instances        |
|  WorkerGroups.TimeoutKeepAlive    | Integer         | Miliseconds to each instances have to answer  |
|  WorkerGroups.Debug               | Boolean         | if true, show your app under your section     |
|  WorkerGroups.Boost.Enabled       | Boolean         | Enable / Disable Boost                        |
|  WorkerGroups.Boost.BoostWorkers  | Boolean         | How many instances will open when boost start |
|  WorkerGroups.Boost.StartTime     | String          | Time to Start the boost                       |
|  WorkerGroups.Boost.EndTime       | String          | Time to End the boost                         |

## 🌱 Wrappers

To your application work with Worker Control, it needs to be implemented the wrapper

| _Language_ | _Status_        | _Link_            | 
| ---------- | --------------- | ----------------- |
|  Delphi    | Done            | [`Delphi Wrapper`](https://github.com/MurilloLazzaretti/worker-delphi-wrapper)|
|  .NET C#   | Done            | [`.NET Wrapper C#`](https://github.com/MurilloLazzaretti/Worker-.NET-Wrapper)|

## 🧬 Resources

🚑  _Crashes Detect_

If any instace controled by the service may crash, it will notice by him and will close the app and open another instace

🔚 _Safe Stop_

When Worker Control needs to close safelly an app, it will send a message to the app and when the app finish all your tasks, it will be closed.

## ⬆️ Update

Replace the executable with the service stopped. `ConfigWorkers.json` stays as it is.

```powershell
Stop-Service WorkerControlService -Force
Copy-Item .\WorkerControl.exe C:\WorkerControl\WorkerControl.exe -Force
Start-Service WorkerControlService
```

Stopping the service asks every application it controls to stop.

## 🔥 Uninstall

```powershell
Stop-Service WorkerControlService -Force
& C:\WorkerControl\WorkerControl.exe /uninstall
Remove-Item C:\WorkerControl -Recurse
```
