unit WorkerControl.StatusMessage;

interface

uses
  Generics.Collections, JSON, WorkerControl.WorkerGroup;

type
  TStatusMessage = class
  public
    class function ToJSON(const pWorkerGroups : TObjectList<TWorkerGroup>) : TJSONObject;
  end;

implementation

uses
  SysUtils,  WorkerControl.Worker;

{ TStatusMessage }

class function TStatusMessage.ToJSON(
  const pWorkerGroups: TObjectList<TWorkerGroup>): TJSONObject;
var
  JSONArray, JSONWorkersArray : TJSONArray;
  JSONWorkerGroup, JSONBoost, JSONWorker : TJSONObject;
  WorkerGroup: TWorkerGroup;
  Worker : TWorker;
begin
  Result := TJSONObject.Create;
  JSONArray := TJSONArray.Create;
  for WorkerGroup in pWorkerGroups do
  begin
    JSONWorkerGroup := TJSONObject.Create;
    JSONBoost := TJSONObject.Create;
    JSONWorkersArray := TJSONArray.Create;
    JSONWorkerGroup.AddPair('Name', TJSONString.Create(WorkerGroup.Name));
    JSONWorkerGroup.AddPair('Enabled', TJSONBool.Create(WorkerGroup.Enabled));
    JSONWorkerGroup.AddPair('ApplicationFullPath', TJSONString.Create(WorkerGroup.ApplicationFullPath));
    JSONWorkerGroup.AddPair('TotalWorkers', TJSONNumber.Create(WorkerGroup.TotalWorkers));
    JSONWorkerGroup.AddPair('MonitoringRate', TJSONNumber.Create(WorkerGroup.MonitoringRate));
    JSONWorkerGroup.AddPair('TimeoutKeepAlive', TJSONNumber.Create(WorkerGroup.TimeoutKeepAlive));
    JSONWorkerGroup.AddPair('LastSyncConfig', TJSONString.Create(DateTimeToStr(WorkerGroup.LastSyncConfig)));
    JSONBoost.AddPair('Enabled', TJSONBool.Create(WorkerGroup.Boost.Enabled));
    JSONBoost.AddPair('BoostWorkers', TJSONNumber.Create(WorkerGroup.Boost.BoostWorkers));
    JSONBoost.AddPair('StartTime', TJSONString.Create(TimeToStr(WorkerGroup.Boost.StartTime)));
    JSONBoost.AddPair('EndTime', TJSONString.Create(TimeToStr(WorkerGroup.Boost.EndTime)));
    JSONWorkerGroup.AddPair('Boost', JSONBoost);
    for Worker in WorkerGroup.Workers do
    begin
      JSONWorker := TJSONObject.Create;
      JSONWorker.AddPair('ProcessId', TJSONNumber.Create(Worker.ProcessId));
      JSONWorker.AddPair('LastKeepAlive', TJSONString.Create(DateTimeToStr(Worker.LastKeepAlive)));
      JSONWorkersArray.AddElement(JSONWorker);
    end;
    JSONWorkerGroup.AddPair('Workers', JSONWorkersArray);
    JSONArray.AddElement(JSONWorkerGroup);
  end;
  Result.AddPair('WorkerGroups', JSONArray);
end;

end.
