## 🇧🇷  Worker Control - Microservices 🇧🇷
 <b>Worker Control</b> is a windows service developed to monitore your microservices. You control the number of applications you need, check a possible crash and make a balance with a boost in determinated time of your choice.

## 🚧 Version 2.0 (.NET)

Worker Control is being rewritten in .NET and this branch (`main`) will become version 2.0.

The Delphi version (1.x) stays available and keeps working:

- Source code: branch [`delphi-v1`](https://github.com/MurilloLazzaretti/Worker-Control/tree/delphi-v1)
- Last Delphi tag: [`v1.1.1`](https://github.com/MurilloLazzaretti/Worker-Control/tree/v1.1.1)

## ⚠️ Warning

Worker Control is in a <b>Beta</b> version for now, if you have any issue, please tell us. 

## ⚙️ Installation

Download and run WorkerControl - Installer.exe, specify the folder to install and next, next, next...
When installation is done, in your services list will be Worker Control service installed and already running (if there was no problem, of course)

## 💉 Dependency

Worker Control needs [`ZapMQ server`](https://github.com/MurilloLazzaretti/ZapMQ) to work. Please, before install Worker Control install ZapMQ server.

## ⚡️ Configuration

After install Worker Control, in the installation folder, there is a file named ConfigWorkers.json. Change it as you need.

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
|  .NET C#   | Coming soon     | |

## 🧬 Resources

🚑  _Crashes Detect_

If any instace controled by the service may crash, it will notice by him and will close the app and open another instace

🔚 _Safe Stop_

When Worker Control needs to close safelly an app, it will send a message to the app and when the app finish all your tasks, it will be closed.

## 🔥 Uninstall

To uninstall the Worker Control, under the folder of the installation, there is a file named "unins000.exe" just run it and next, next next...