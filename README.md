## 🇧🇷  Worker Control - Microservices 🇧🇷

<b>Worker Control</b> is a Windows service that keeps your applications running. You say how many instances of each one you need; it starts them, replaces the ones that go away or stop answering, and adds instances during the hours you choose.

## 🧭 Versions

| Version | Where | Status |
| ------- | ----- | ------ |
| 2.x (.NET) | this branch (`main`) | in development |
| 1.x (Delphi) | branch [`delphi-v1`](https://github.com/MurilloLazzaretti/Worker-Control/tree/delphi-v1), last tag [`v1.1.1`](https://github.com/MurilloLazzaretti/Worker-Control/tree/v1.1.1) | maintenance |

Version 2.x is a rewrite. It talks to the applications exactly as 1.x did and reads the same `ConfigWorkers.json`, so nothing changes in them. What it is meant to do, and in which order, is in [`docs/ESPECIFICACAO-2.0.md`](docs/ESPECIFICACAO-2.0.md) (in Portuguese). The service is done, with its history and its administration over ZapMQ, and is operated from the web panel of [ZapMQ](https://github.com/MurilloLazzaretti/ZapMQ) 2.2: groups and processes live, actions, configuration, history and the trace of each process. Management Studio 1.x keeps working against it.

## 🧬 Resources

🚑 _Crash detection_

An instance that goes away is replaced right then. An instance that stops answering its keep-alive is ended and replaced.

🔚 _Safe stop_

To take an instance out, the service asks it to stop and waits for it to finish its work and leave. One that does not leave in time is ended.

🪜 _Staggered start_

Instances are started a few at a time, and a new instance has a grace period to answer for the first time. A machine starting everything at once is no longer too busy for any of them to get ready.

🧲 _Nothing duplicated_

The service remembers the instances it is running. Started again, it finds them instead of starting others.

🛡 _A broker failure ends nobody_

While ZapMQ is unreachable, an unanswered keep-alive is not held against any instance. Instances that go away are still replaced.

🌀 _No restart loop_

A group whose instances keep failing right after starting waits longer and longer between attempts, and says so in the log.

🚀 _Boost_

Windows of time in which a group runs extra instances, on the days of the week you choose. A window may cross midnight.

📈 _Scaling by the queue_

A group gets extra instances while its queue in ZapMQ has messages piling up, and gives them back some time after it empties.

♻️ _Recycling_

At a time of your choice, the instances of a group are replaced one by one, the new one up before the old one leaves. The same can be asked for at any moment, for a group or for a single instance.

🗂 _History and health_

Every start, exit, replacement and stop is kept, along with processor, memory and keep-alive response time of each instance.

🎛 _Administration over ZapMQ_

Status, configuration, manual actions, history and health are available to whoever reaches ZapMQ, from any machine. It is what the web panel will use.

## 💉 Dependency

Worker Control needs a [`ZapMQ server`](https://github.com/MurilloLazzaretti/ZapMQ): it is through ZapMQ that it talks to the applications it controls. Install ZapMQ first.

## 🌱 Wrappers

For an application to work with Worker Control, it uses the wrapper:

| _Language_ | _Status_ | _Link_ |
| ---------- | -------- | ------ |
| Delphi     | Done     | [`Delphi Wrapper`](https://github.com/MurilloLazzaretti/worker-delphi-wrapper) |
| .NET C#    | Done     | [`.NET Wrapper C#`](https://github.com/MurilloLazzaretti/Worker-.NET-Wrapper) |

## 📋 Requirements

- Windows Server 2016 or later, or Windows 10 or later, 64-bit.
- A ZapMQ server the machine can reach.
- To build: the [.NET 10 SDK](https://dotnet.microsoft.com/download). The machine that runs the service needs nothing installed: the executable carries the runtime.

## 🔨 Build

From the repository root:

```
dotnet publish src/WorkerControl.Service -c Release -r win-x64 -o publish/win-x64
```

The folder `publish/win-x64` now has the two files the service needs:

| File | What it is |
| ---- | ---------- |
| `WorkerControl.exe` | the service |
| `e_sqlite3.dll` | the SQLite engine, used for the history |

## ⚡️ Configuration

The settings are in `ConfigWorkers.json`, in the same folder as `WorkerControl.exe`. A sample is in the repository root.

```json
{
    "ZapMQHost" : "localhost",
    "ZapMQPort" : 5679,
    "RateLoadConfig" : 180000,
    "WorkerGroups" : [
        {
            "Enabled" : true,
            "Name" : "Orders",
            "ApplicationFullPath" : "C:\\Apps\\Orders\\Orders.exe",
            "TotalWorkers" : 2,
            "MonitoringRate": 30000,
            "TimeoutKeepAlive" : 15000,
            "Boost" : {
                "Enabled" : false,
                "BoostWorkers" : 3,
                "StartTime" : "12:00:00",
                "EndTime" : "12:19:00"
            }
        }
    ]
}
```

The file is read again the moment it changes. A file with anything wrong in it is refused as a whole: the settings in use stay, and the log says what was wrong.

🍬 _Settings of 1.x_

| _Property_ | _Value_ | _Description_ |
| ---------- | ------- | ------------- |
| `ZapMQHost` | Text | Address of the ZapMQ server. Changing it takes a restart of the service |
| `ZapMQPort` | Number | Port of the ZapMQ server. Changing it takes a restart of the service |
| `RateLoadConfig` | Milliseconds | How often the file is read even if nothing announced a change |
| `WorkerGroups.Enabled` | true/false | A disabled group has its instances stopped |
| `WorkerGroups.Name` | Text | Name of the group; no two groups may share one |
| `WorkerGroups.ApplicationFullPath` | Text | Full path of the executable |
| `WorkerGroups.TotalWorkers` | Number | Instances to keep running |
| `WorkerGroups.MonitoringRate` | Milliseconds | Interval between keep-alives |
| `WorkerGroups.TimeoutKeepAlive` | Milliseconds | How long an instance already up may take to answer a keep-alive |
| `WorkerGroups.Boost.Enabled` | true/false | Turns the boost window on |
| `WorkerGroups.Boost.BoostWorkers` | Number | Extra instances during the window |
| `WorkerGroups.Boost.StartTime` | `hh:mm:ss` | Start of the window |
| `WorkerGroups.Boost.EndTime` | `hh:mm:ss` | End of the window. Earlier than the start means the window crosses midnight |

🆕 _New in 2.0_

All optional. The ones marked "root or group" may be given once at the root, for every group, and again inside a group, for that one only.

| _Property_ | _Where_ | _Default_ | _Description_ |
| ---------- | ------- | --------- | ------------- |
| `StartBatchSize` | root | 4 | Instances started per batch, all groups together |
| `StartBatchIntervalMs` | root | 2000 | Interval between batches |
| `StartupGraceMs` | root or group | 60000 | How long a new instance has to answer its first keep-alive |
| `SafeStopTimeoutMs` | root or group | 30000 | How long an instance asked to stop has to leave before it is ended |
| `CrashWindowMs` | root or group | 60000 | An instance that fails sooner than this after starting counts as a quick failure |
| `CrashLimit` | root or group | 3 | Quick failures in a row before the group starts waiting between attempts |
| `Arguments` | group | empty | Arguments passed to the executable |
| `WorkingDirectory` | group | folder of the executable | Working folder of the instance |

| `EventRetentionDays` | root | 30 | How long the history of events is kept |
| `HealthRetentionHours` | root | 24 | How long the health measurements are kept |
| `BoostWindows` | group | none | Boost windows, see below |
| `QueueScaling` | group | none | Scaling by the queue, see below |
| `Recycle` | group | none | Scheduled recycling, see below |

`TimeoutKeepAlive` no longer has to cover the time an application takes to start; that is what `StartupGraceMs` is for.

```json
"BoostWindows": [
    { "Workers": 3, "StartTime": "22:00:00", "EndTime": "02:00:00", "Days": ["mon", "tue", "wed", "thu", "fri"] }
],
"QueueScaling": { "Queue": "Orders", "PendingPerWorker": 50, "MaxWorkers": 8, "CooldownMs": 120000 },
"Recycle": { "Time": "03:00:00", "Days": ["sun"] }
```

- **`BoostWindows`**: `Workers` extra instances between `StartTime` and `EndTime`. `Days` takes `mon` to `sun`; left out, it is every day. A window that crosses midnight belongs to the day it starts on. Windows that overlap do not add up: the largest wins. The `Boost` of 1.x keeps working, as one more window.
- **`QueueScaling`**: one extra instance for each `PendingPerWorker` messages waiting in `Queue`, never taking the group above `MaxWorkers`. The extra ones leave `CooldownMs` after the queue stops asking for them. Needs ZapMQ 2.x.
- **`Recycle`**: at `Time`, on `Days`, every instance of the group is replaced, one at a time.

Management Studio 1.x writes the file with the settings it knows. Saving from it drops the new ones.

### Windows services

Services of the machine that are not started by Worker Control can be watched by it: state, uptime, processor and memory, an optional check, and start, stop and restart from the panel. They are chosen in the panel, from the list of what is installed; what is chosen is written here:

```json
"Services": {
  "SuggestFrom": ["D:\\Apps"],
  "Items": [
    { "Name": "MyService" },
    { "Name": "OtherService", "AutoRestart": true, "StopTimeoutMs": 30000, "Check": { "Tcp": "localhost:9100" } }
  ]
}
```

| Key | Default | Meaning |
| --- | ------- | ------- |
| `SuggestFrom` | none | Folders whose services are offered first to whoever is choosing |
| `Name` | | The name of the service in Windows (the short one) |
| `AutoRestart` | false | Starts the service again when it stops by itself with an error, waiting longer each time it happens in a row. A service somebody stopped stays stopped |
| `StopTimeoutMs` | 30000 | How long a service asked to stop is waited for. After that the panel says so; the service is not ended by force |
| `Check` | none | `Tcp` (a host and port that has to take a connection) or `Url` (an address that has to answer 2xx), tried every 15 seconds while the service runs |
| `LogFiles` | none | Folder and file name pattern of the log of the service |

### Traffic

The traffic through a reverse proxy can be measured from its access log, without touching any application: how many requests, how fast and with how many errors, by application, endpoint and instance.

The proxy has to write one JSON line per request. For NGINX:

```nginx
log_format zapmq escape=json
    '{"t":"$time_iso8601","ms":$msec,"ip":"$remote_addr","h":"$host",'
    '"m":"$request_method","u":"$request_uri","s":$status,"b":$body_bytes_sent,'
    '"rt":$request_time,"ua":"$upstream_addr","us":"$upstream_status",'
    '"ut":"$upstream_response_time","ref":"$http_referer"}';
access_log logs/access.zapmq.log zapmq;
```

```json
"Traffic": {
  "AccessLog": "C:\\nginx\\logs\\access.zapmq.log",
  "ReopenCommand": "C:\\nginx\\nginx.exe -s reopen",
  "Ignore": ["/panel/"]
}
```

| Key | Default | Meaning |
| --- | ------- | ------- |
| `AccessLog` | | The file the proxy writes, in the format above |
| `ReopenCommand` | none | What makes the proxy open its log again. With it, the file is renamed when it passes `RotateAtMb` and the proxy starts another; without it, the file is never touched |
| `RotateAtMb` | 100 | Size at which the file is put aside |
| `KeepFiles` | 5 | Files put aside that are kept |
| `RetentionDays` | 30 | How long the numbers are kept. They are by the minute for two days and by the hour after that |
| `MaxRoutes` | 2000 | Distinct routes counted per day; beyond that they are added up as one |
| `KeepErrors` | 500 | Last answers with a server error (5xx) kept to be looked at |
| `GroupBy` | `api`, `mfe` | First segments of a path after which the next one still names the application |
| `Ignore` | none | Beginnings of paths that are not counted |
| `Routes` | none | Routes written by hand, such as `/api/orders/{code}/items`, for what the general rule does not tell apart |

Nothing of a request is kept but its route and the screen it came from: the query string is dropped, and numbers, identifiers and long or encoded segments become `{id}`. The screen is what the browser says in the referer, which is what tells how much each part of a web application is used. The numbers are in `traffic.db`, next to `ConfigWorkers.json`.

### Web application

A web application made of modules published as folders (micro frontends) can be watched too: which modules there are, the version each one says it is in, whether each one answers, and when each one was published.

```json
"Frontends": [
  { "Name": "App", "Root": "D:\\www\\app", "BaseUrl": "http://app.example" }
]
```

| Key | Default | Meaning |
| --- | ------- | ------- |
| `Name` | | How the application is called in the panel |
| `Root` | | The folder the web server serves it from |
| `BaseUrl` | none | Where it answers. Each module is asked for there once a minute; without it, a module is only checked on disk |
| `Host` | none | The name of the site, when `BaseUrl` is only the address of the machine |
| `Manifest` | `assets/mf.manifest.json` | The file with the modules: an object of name and path of the entry file |
| `VersionFile` | `version.json` | The file beside the entry file of each module, with `nome`, `build` and `versions` (`version`, `date`, `descriptions`), the newest first |

What changes on disk is kept as a publication in `frontends.json`, next to `ConfigWorkers.json`.

### Database

The one database instance the applications of the machine use can be watched too: whether it
answers, processor and memory, sessions per application, what is running, who blocks whom,
databases, disks, backups, jobs and the statements that cost the most. Only SQL Server for now.

```json
"Database": {
  "Name": "DEV",
  "Server": "host\\instance",
  "User": "user",
  "Password": "typed once",
  "Databases": ["MyDatabase"],
  "SampleSeconds": 30,
  "BlockingSeconds": 30,
  "BackupHours": 0,
  "DiskFreePercent": 10,
  "RetentionDays": 7
}
```

- **No row of any table is ever read.** Every statement sent to the instance is in
  `Database/Queries.cs` and reads only its catalog, its management views and the history of
  backups and jobs; a test refuses anything else. The text of running statements is given
  without the values written in it.
- **The password does not stay as typed.** On the first read it is replaced in the file by one
  protected with the data protection of Windows under the key of the machine (`dpapi:...`). Type
  a new one over it to change it. Without `User`, the account of the service is used.
- A part the user of the connection may not read is left out and named; the rest still comes.
- With `Databases`, only those are shown: their sessions, what runs in them, their disks, backups
  and costly statements. An instance is often shared with other environments. Without it, all
  the databases are. The costly statements never include what runs in the system databases,
  which is where monitors (this one too) ask their questions.
- The objects of the databases named can be listed too: tables, views, procedures, functions and
  user-defined types, each with its columns or parameters, indexes, constraints, what it uses,
  who uses it and the script that creates it. The script of a view, procedure or function is the
  text the instance keeps; the one of a table or type is written from the catalog. Each comes
  with a fingerprint of its script. A database that is not named is refused.
- Every `ObjectScanMinutes` (10 by default; zero never does) the objects are looked at for what
  changed: created, altered, renamed and dropped, each kept with the script before and after for
  `ObjectHistoryDays` (365), in `objects.db`. Only what the instance says was touched is read
  again, and an object is the same while its script is. The first look at a database only
  records how it is. Who made the change is taken from the default trace of the instance, when
  it can be read.
- One thing writes to the database, and only one: the item of a package of changes somebody
  approved on the panel (`DatabaseApply`). It goes to a database that is named in `Databases`
  and nowhere else, each item in a transaction of its own. An object that exists is altered
  instead of created again; a table or a type that exists is refused.
- `BackupHours` zero does not look at backups. The history of the charts is kept in `database.db`.

### Transport

What runs on the machine can be replaced by what a package of changes brings, approved on the
panel: a group of workers, a watched Windows service, an application behind the web server or a
module of the web application. The targets are worked out from the rest of this file; what
cannot be, or is to be said otherwise, goes here.

```json
"Transport": {
  "KeepVersions": 3,
  "Keep": ["appsettings*.json", "web.config", "ConfigWorkers.json", "*.db", "*.db-wal", "*.db-shm", "logs/"],
  "Targets": [
    { "Kind": "service", "Name": "MyWrappedService", "Paths": ["D:\\Apps\\my-service"] },
    { "Kind": "api", "Name": "Orders", "Paths": ["D:\\www\\api\\Orders"], "Sites": ["Orders 1", "Orders 2"] }
  ]
}
```

- Replacing a target stops whatever runs from its folder, keeps a copy of the folder under
  `transport/backup`, makes the folder hold exactly the files of the package and starts it
  again. What is in `Keep` is never packed nor replaced. If the files cannot all be put, the
  copy is put back first.
- A new version is left in the inbox, `transport/inbox/<kind>/<name>` beside the service
  (`"Inbox"` says another place): a folder, or a zip, with the name of the target under
  `worker`, `service`, `api` or `frontend`. The panel takes it from there into a package.
- A service that is run by a wrapper says nothing of where its program is: name its folder here.
- Sites are taken off the air by stopping their application pools with `appcmd.exe`, which asks
  for the service to run with enough rights to do it.

## ⚙️ Installation

Run the commands in a PowerShell window opened as administrator. The examples use the folder `C:\WorkerControl`.

1. Copy `WorkerControl.exe`, `e_sqlite3.dll` and your `ConfigWorkers.json` to `C:\WorkerControl`. If you use Management Studio, it goes in the same folder: it edits that same file.

2. Register the service and start it:

```powershell
sc.exe create WorkerControlService binPath= "C:\WorkerControl\WorkerControl.exe" DisplayName= "WorkerControl" start= auto depend= ZapMQ
sc.exe description WorkerControlService "Keeps the configured applications running"
Start-Service WorkerControlService
```

Leave `depend= ZapMQ` out when ZapMQ runs on another machine, and replace `ZapMQ` with the name its service has on yours.

The service runs under the Local System account, and so does every application it starts. The names above are the ones 1.x used, which Management Studio 1.x looks for.

3. Check that it is running:

```powershell
Get-Service WorkerControlService
Get-Content C:\WorkerControl\logs\workercontrol-*.log -Tail 20
```

The applications of the enabled groups appear in the Task Manager within seconds.

## 🔁 Coming from 1.x

The file `ConfigWorkers.json` is used as it is. With the 1.x service installed under the same name, point it at the new executable instead of creating another:

```powershell
Stop-Service WorkerControlService -Force      # stops the applications too
Copy-Item "C:\Program Files (x86)\WorkerControl\ConfigWorkers.json" C:\WorkerControl\
sc.exe config WorkerControlService binPath= "C:\WorkerControl\WorkerControl.exe"
Start-Service WorkerControlService
```

To go back, stop the service and run `sc.exe config` again with the path of the 1.x executable.

What is different for the applications: an instance now starts in the folder of its own executable, where 1.x started it in the folder of the service. An application that depended on that needs `WorkingDirectory`.

## 🎛 Administration

Requests go to the queue `WorkerControlAdmin`, as RPC messages. The two commands of 1.x (`{"Message": "CurrentWorkers"}` and `{"Message": "ReloadConfig"}`) are answered as 1.x answered them. The commands of 2.0 carry `Command`:

```json
{ "Command": "SetGroupWorkers", "Group": "Orders", "TotalWorkers": 4, "By": "ana" }
```

```json
{ "Ok": true }
{ "Ok": false, "Error": { "Code": "not-found", "Message": "There is no group named \"Orders\"" } }
```

| _Command_ | _Fields_ | _What it does_ |
| --------- | -------- | -------------- |
| `Status` | | The service, its groups and workers: state, how many are wanted and why, health |
| `GetConfig` | | The configuration file, as text and as an object |
| `SetConfig` | `Config` | Replaces the configuration file, after validating it |
| `SetGroupEnabled` | `Group`, `Enabled` | Enables or disables a group |
| `SetGroupWorkers` | `Group`, `TotalWorkers` | Changes the number of instances of a group |
| `RestartWorker` | `ProcessId` | Replaces one instance; it leaves once its substitute is up |
| `RestartGroup` | `Group` | Replaces every instance of a group, one at a time |
| `Events` | `Group`, `Kind`, `From`, `To`, `Limit` (all optional) | The history, most recent first |
| `Health` | `Group`, `ProcessId`, `From`, `To`, `Limit` (all optional) | The measurements, oldest first |
| `DetachAndStop` | | Stops the service leaving the instances running |

`By`, optional in any command that changes something, is recorded in the history. The changes to groups are written to `ConfigWorkers.json`, keeping the previous version as `ConfigWorkers.json.bak`.

## 📜 Log and history

One file per day in `logs`, next to the executable. Every start, exit, replacement and stop is there, with the group and the process id:

```
WorkerStarted [Orders] pid 4812: C:\Apps\Orders\Orders.exe
WorkerUp [Orders] pid 4812: answered after 1.4 s
WorkerCrashed [Orders] pid 4812: exit code 1, after 37 min
WorkerHung [Orders] pid 5120: no answer to the keep-alive in 15 s
```

Next to the executable there are also `workercontrol.db`, a SQLite file with the history of events and the health measurements, and `state.json`, with the instances being supervised.

## ⬆️ Update

Replace the executable with the service stopped. `ConfigWorkers.json` stays as it is.

```powershell
Stop-Service WorkerControlService -Force
Copy-Item .\WorkerControl.exe C:\WorkerControl\WorkerControl.exe -Force
Start-Service WorkerControlService
```

Stopping the service asks every application it controls to stop, and waits for them.

To update Worker Control itself **without stopping the applications**, create an empty file named `detach.flag` in its folder before stopping the service. It then leaves them running and finds them again when it starts:

```powershell
New-Item C:\WorkerControl\detach.flag -ItemType File
Stop-Service WorkerControlService -Force
Copy-Item .\WorkerControl.exe C:\WorkerControl\WorkerControl.exe -Force
Start-Service WorkerControlService
```

## 🔥 Uninstall

```powershell
Stop-Service WorkerControlService -Force
sc.exe delete WorkerControlService
Remove-Item C:\WorkerControl -Recurse
```

## 🧪 Development

```
dotnet test
```

`tests/WorkerControl.Core.Tests` exercises the supervisor with simulated processes, broker and clock. `tests/WorkerControl.Service.Tests` runs the service with real processes (`tests/TestWorker`, a worker that misbehaves on request) and a real ZapMQ server, built from the [`ZapMQ`](https://github.com/MurilloLazzaretti/ZapMQ) repository. They expect it cloned next to this one; to use another place, add `-p:ZapMQServerProject=<path to ZapMQ.Server.csproj>`.

`lib/ZapMQWrapper.dll` is the ZapMQ .NET wrapper 2.0, built from [its repository](https://github.com/MurilloLazzaretti/ZapMQ-.NET-Wrapper).
