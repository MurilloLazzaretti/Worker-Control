unit ManagementStudio.ServiceManager;

interface

type
  TServiceManager = class
  public
    class procedure StartService;
    class procedure StopService;
    class function GetServiceState : string;
  end;

implementation

uses
  WinSvc, Types, SysUtils, ShellApi, Windows, Vcl.Forms;

{ TServiceManager }

class function TServiceManager.GetServiceState: string;
var
  SCManHandle, SvcHandle: SC_Handle;
  SS: TServiceStatus;
  dwStat: DWORD;
begin
  dwStat := 0;
  SCManHandle := OpenSCManager(nil, nil, SC_MANAGER_CONNECT);
  if (SCManHandle > 0) then
  begin
    SvcHandle := OpenService(SCManHandle, 'WorkerControlService', SERVICE_QUERY_STATUS);
    if (SvcHandle > 0) then
    begin
      if (QueryServiceStatus(SvcHandle, SS)) then
        dwStat := ss.dwCurrentState;
      CloseServiceHandle(SvcHandle);
    end;
    CloseServiceHandle(SCManHandle);
  end;
  case dwStat of
    1:
      Result := 'SERVICE_STOPPED';
    2:
      Result := 'SERVICE_START_PENDING';
    3:
      Result := 'SERVICE_STOP_PENDING';
    4:
      Result := 'SERVICE_RUNNING';
    5:
      Result := 'SERVICE_CONTINUE_PENDING';
    6:
      Result := 'SERVICE_PAUSE_PENDING';
    7:
      Result := 'SERVICE_PAUSED';
    8:
      Result := 'SERVICE_NOT_INSTALLED';
    else
      Result := 'ERROR';
  end;
end;

class procedure TServiceManager.StartService;
begin
  ShellExecute(0, 'runas', 'net.exe', PChar('start WorkerControl'), nil, SW_HIDE);
end;

class procedure TServiceManager.StopService;
begin
  ShellExecute(0, 'runas', 'net.exe', PChar('stop WorkerControl'), nil, SW_HIDE);
end;

end.
