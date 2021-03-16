unit uMain;

interface

uses
  Winapi.Windows, Winapi.Messages, System.SysUtils, System.Variants, System.Classes, Vcl.Graphics,
  Vcl.Controls, Vcl.Forms, Vcl.Dialogs, Vcl.ComCtrls, Vcl.Menus,
  System.ImageList, Vcl.ImgList, Vcl.StdCtrls, Vcl.ExtCtrls, ZapMQ.Wrapper, JSON,
  Vcl.WinXCtrls, Vcl.WinXPickers, Vcl.Samples.Spin, ManagementStudio.Config,
  ManagementStudio.Worker, Generics.Collections, Vcl.Buttons, ZapMQ.Message.JSON,
  IdBaseComponent, IdComponent, IdCustomTCPServer,
  IdTCPServer, IdContext, System.Win.ScktComp;

type
  TFrmMain = class(TForm)
    TreeViewWorkersGroups: TTreeView;
    MainMenu: TMainMenu;
    Configuration1: TMenuItem;
    Quit1: TMenuItem;
    StatusBar1: TStatusBar;
    NewGroup1: TMenuItem;
    Edit1: TMenuItem;
    Remove1: TMenuItem;
    ImageListTreeView: TImageList;
    Refresh1: TMenuItem;
    Service1: TMenuItem;
    Start1: TMenuItem;
    Stop2: TMenuItem;
    PageControl1: TPageControl;
    TabSheetConfiguration: TTabSheet;
    TabSheetTrace: TTabSheet;
    PanelGroupInformation: TPanel;
    GroupBoxButtonsEdit: TGroupBox;
    btnCancelEditInformation: TButton;
    btnSaveGroupInformations: TButton;
    GroupBox1: TGroupBox;
    GroupBox2: TGroupBox;
    Label1: TLabel;
    Label2: TLabel;
    Label6: TLabel;
    RadioGroupBoost: TRadioGroup;
    txtStartTimeBoost: TTimePicker;
    txtEndTimeBoost: TTimePicker;
    txtBoostWorkers: TSpinEdit;
    OpenDialog1: TOpenDialog;
    GroupBox3: TGroupBox;
    RadioGroupWorker: TRadioGroup;
    txtName: TLabeledEdit;
    txtApplicationFullPath: TLabeledEdit;
    BrowseApplicationFullPath: TButton;
    txtTimeoutKeepAlive: TSpinEdit;
    Label5: TLabel;
    txtMonitoringRate: TSpinEdit;
    Label4: TLabel;
    txtTotalWorkers: TSpinEdit;
    Label3: TLabel;
    ImageListButtons: TImageList;
    GroupBox4: TGroupBox;
    btnStopTrace: TButton;
    btnStartTrace: TButton;
    PageControlTrace: TPageControl;
    TabSheet1: TTabSheet;
    TabSheet2: TTabSheet;
    TabSheet3: TTabSheet;
    TabSheet4: TTabSheet;
    TabSheet5: TTabSheet;
    TabSheet6: TTabSheet;
    TabSheet7: TTabSheet;
    TabSheet8: TTabSheet;
    TabSheet9: TTabSheet;
    TabSheet10: TTabSheet;
    Memo1: TMemo;
    Memo2: TMemo;
    Memo3: TMemo;
    Memo4: TMemo;
    Memo5: TMemo;
    Memo6: TMemo;
    Memo7: TMemo;
    Memo8: TMemo;
    Memo9: TMemo;
    Memo10: TMemo;
    Label7: TLabel;
    lblGroupName: TLabel;
    Label8: TLabel;
    lblTotalWorkers: TLabel;
    GroupBox5: TGroupBox;
    procedure FormCreate(Sender: TObject);
    procedure FormClose(Sender: TObject; var Action: TCloseAction);
    procedure Edit1Click(Sender: TObject);
    procedure btnCancelEditInformationClick(Sender: TObject);
    procedure btnSaveGroupInformationsClick(Sender: TObject);
    procedure Refresh1Click(Sender: TObject);
    procedure TreeViewWorkersGroupsChange(Sender: TObject; Node: TTreeNode);
    procedure NewGroup1Click(Sender: TObject);
    procedure BrowseApplicationFullPathClick(Sender: TObject);
    procedure Remove1Click(Sender: TObject);
    procedure Stop2Click(Sender: TObject);
    procedure Start1Click(Sender: TObject);
    procedure btnStartTraceClick(Sender: TObject);
    procedure btnStopTraceClick(Sender: TObject);
    procedure Quit1Click(Sender: TObject);
  private
    FWorkerControlConfig : TConfig;
    FWorkers : TObjectList<TWorker>;
    FZapMQWrapper : TZapMQWrapper;
    procedure AssertWorkers;
    procedure ClearWorkersNode(const pWorkerGroupName : string);
    procedure CheckSynchronizePending(const pWorkerGroupName : string;
      const pLastSyncServer : TDateTime);
    function GetSynchronizePending(const pWorkerGroupName : string) : boolean;
    procedure SetLastSynchronize(const pWorkerGroupName : string);
    procedure LoadWorkGroupInformation(const pIndex : integer);
    procedure CurrentWorkersHandler(pMessage : TJSONObject; var pProcessing : boolean);
    procedure ReloadConfigHandler(pMessage : TJSONObject; var pProcessing : boolean);
    procedure LoadConfigFile;
    procedure LoadCurrentWorkers;
    procedure ClearGroupInformation;
    procedure RPCExpired(const pMessage : TZapJSONMessage);
  public
    { Public declarations }
  end;

var
  FrmMain: TFrmMain;

implementation

uses
  System.UITypes, ManagementStudio.ServiceManager;

{$R *.dfm}

{ TFrmMain }

procedure TFrmMain.AssertWorkers;
var
  NodeGroup, NodeWorker, NodeInfoWorker : TTreeNode;
  Worker: TWorker;
begin
  TreeViewWorkersGroups.Items.BeginUpdate;
  for NodeGroup in TreeViewWorkersGroups.Items do
  begin
    if NodeGroup.Level = 1 then
    begin
      NodeGroup.Text := StringReplace(NodeGroup.Text, '(Sync pending)', '', [rfReplaceAll]);
      if GetSynchronizePending(NodeGroup.Text) then
      begin
        NodeGroup.Text := NodeGroup.Text + '(Sync pending)';
      end
      else
      begin
        for Worker in FWorkers do
        begin
          if Worker.GroupName = NodeGroup.Text then
          begin
            NodeWorker := TreeViewWorkersGroups.Items.AddChild(NodeGroup, Worker.ApplicationName);
            NodeWorker.ImageIndex := 2;
            NodeWorker.SelectedIndex := 2;
            NodeInfoWorker := TreeViewWorkersGroups.Items.AddChild(NodeWorker, 'Id: ' + Worker.ProcessId.ToString);
            NodeInfoWorker.ImageIndex := 3;
            NodeInfoWorker.SelectedIndex := 3;
            NodeInfoWorker := TreeViewWorkersGroups.Items.AddChild(NodeWorker, 'KeepAlive: ' + DateTimeToStr(Worker.LastKeepAlive));
            NodeInfoWorker.ImageIndex := 3;
            NodeInfoWorker.SelectedIndex := 3;
          end;
        end;
      end;
    end;
  end;
  TreeViewWorkersGroups.Items.GetFirstNode.Selected := True;
  TreeViewWorkersGroups.Items.EndUpdate;
  TreeViewWorkersGroups.FullExpand;
end;

procedure TFrmMain.btnCancelEditInformationClick(Sender: TObject);
begin
  GroupBoxButtonsEdit.Visible := False;
  PanelGroupInformation.Enabled := False;
  TreeViewWorkersGroups.Enabled := True;
  if TreeViewWorkersGroups.Selected.Level = 0 then
  begin
    PageControl1.Visible := False;
  end;
end;

procedure TFrmMain.btnSaveGroupInformationsClick(Sender: TObject);
var
  GroupName : string;
  GroupConfig: TWorkerGroupConfig;
  Json : TJSONObject;
  NewNode : TTreeNode;
begin
  TreeViewWorkersGroups.Enabled := True;
  GroupBoxButtonsEdit.Visible := False;
  PanelGroupInformation.Enabled := False;
  if TreeViewWorkersGroups.Selected.Level = 0 then
  begin
    GroupConfig := TWorkerGroupConfig.Create;
    GroupConfig.Enabled := RadioGroupWorker.ItemIndex = 0;
    GroupConfig.Name := txtName.Text;
    GroupConfig.ApplicationFullPath := txtApplicationFullPath.Text;
    GroupConfig.TotalWorkers := txtTotalWorkers.Value;
    GroupConfig.MonitoringRate := txtMonitoringRate.Value;
    GroupConfig.TimeoutKeepAlive := txtTimeoutKeepAlive.Value;
    GroupConfig.Boost.Enabled := RadioGroupBoost.ItemIndex = 0;
    GroupConfig.Boost.BoostWorkers := txtBoostWorkers.Value;
    GroupConfig.Boost.StartTime := txtStartTimeBoost.Time;
    GroupConfig.Boost.EndTime := txtEndTimeBoost.Time;
    FWorkerControlConfig.WorkerGroupsConfig.Add(GroupConfig);
    NewNode := TreeViewWorkersGroups.Items.AddChild(TreeViewWorkersGroups.Items.GetFirstNode,
      GroupConfig.Name);
    NewNode.ImageIndex := 1;
    NewNode.SelectedIndex := 1;
    NewNode.Selected := True;
    FWorkerControlConfig.ToFile('ConfigWorkers.json');
  end
  else
  begin
    GroupName := TreeViewWorkersGroups.Selected.Text;
    SetLastSynchronize(GroupName);
    ClearWorkersNode(GroupName);
    for GroupConfig in FWorkerControlConfig.WorkerGroupsConfig do
    begin
      if GroupName = GroupConfig.Name then
      begin
        GroupConfig.Enabled := RadioGroupWorker.ItemIndex = 0;
        GroupConfig.Name := txtName.Text;
        GroupConfig.ApplicationFullPath := txtApplicationFullPath.Text;
        GroupConfig.TotalWorkers := txtTotalWorkers.Value;
        GroupConfig.MonitoringRate := txtMonitoringRate.Value;
        GroupConfig.TimeoutKeepAlive := txtTimeoutKeepAlive.Value;
        GroupConfig.Boost.Enabled := RadioGroupBoost.ItemIndex = 0;
        GroupConfig.Boost.BoostWorkers := txtBoostWorkers.Value;
        GroupConfig.Boost.StartTime := txtStartTimeBoost.Time;
        GroupConfig.Boost.EndTime := txtEndTimeBoost.Time;
        Break;
      end;
    end;
    FWorkerControlConfig.ToFile('ConfigWorkers.json');
  end;
  Json := TJSONObject.Create;
  try
    Json.AddPair('Message', 'ReloadConfig');
    FZapMQWrapper.SendRPCMessage('WorkerControlAdmin', Json, ReloadConfigHandler, 30000);
    TreeViewWorkersGroups.FullCollapse;
    TreeViewWorkersGroups.Enabled := False;
    TreeViewWorkersGroups.Items.GetFirstNode.Text :=
      TreeViewWorkersGroups.Items.GetFirstNode.Text;
  finally
    Json.Free;
  end;
end;

procedure TFrmMain.btnStartTraceClick(Sender: TObject);
var
  Worker: TWorker;
  TotalWorkers : integer;
  Workers : TObjectList<TWorker>;
  I: Integer;
  Memo : TMemo;
begin
  btnStartTrace.Enabled := False;
  btnStopTrace.Enabled := True;
  TotalWorkers := StrToInt(lblTotalWorkers.Caption);
  if TotalWorkers <= 0 then
  begin
    MessageDlg('There is no Workers available to start trace!', mtError,
      [mbOK], 0);
  end
  else if TotalWorkers > 10 then
  begin
    MessageDlg('There are more than 10 workers running, please reconfigure to up to 10 workes and try again!', mtError,
      [mbOK], 0);
  end
  else
  begin
    TreeViewWorkersGroups.Enabled := False;
    Workers := TObjectList<TWorker>.Create(False);
    try
      for Worker in FWorkers do
      begin
        if Worker.GroupName = lblGroupName.Caption then
        begin
          Workers.Add(Worker);
        end
      end;
      for I := 0 to Pred(Workers.Count) do
      begin
        PageControlTrace.Pages[I].Caption := Workers[I].ProcessId.ToString;
        Memo := TMemo(FindComponent('Memo' + (I + 1).ToString));
        Workers[i].StartTrace(Memo, FZapMQWrapper);
      end;
    finally
      Workers.Free;
    end
  end
end;

procedure TFrmMain.btnStopTraceClick(Sender: TObject);
var
  I: Integer;
  Worker : TWorker;
  Workers : TObjectList<TWorker>;
begin
  btnStopTrace.Enabled := False;
  btnStartTrace.Enabled := True;
  TreeViewWorkersGroups.Enabled := True;
  for I := 0 to 9 do
  begin
    PageControlTrace.Pages[I].Caption := '';
    TMemo(FindComponent('Memo' + (I + 1).ToString)).Lines.Clear;
  end;
  Workers := TObjectList<TWorker>.Create(False);
  try
    for Worker in FWorkers do
    begin
      if Worker.GroupName = lblGroupName.Caption then
      begin
        Workers.Add(Worker);
      end
    end;
    for I := 0 to Pred(Workers.Count) do
    begin
      Workers[i].StopTrace;
    end;
  finally
    Workers.Free;
  end
end;

procedure TFrmMain.BrowseApplicationFullPathClick(Sender: TObject);
begin
  if OpenDialog1.Execute then
  begin
    if FileExists(OpenDialog1.FileName) then
    begin
      txtApplicationFullPath.Text := OpenDialog1.FileName;
    end
    else
      MessageDlg('Invalid file name !', mtError, [mbOK], 0);
  end;
end;

procedure TFrmMain.CheckSynchronizePending(const pWorkerGroupName: string;
  const pLastSyncServer: TDateTime);
var
  WorkerGroup: TWorkerGroupConfig;
begin
  for WorkerGroup in FWorkerControlConfig.WorkerGroupsConfig do
  begin
    if WorkerGroup.Name = pWorkerGroupName then
    begin
      if pLastSyncServer < WorkerGroup.LastSyncConfig then
      begin
        WorkerGroup.SynchronizePendind := True;
      end
      else
      begin
        WorkerGroup.SynchronizePendind := False;
      end;
    end;
  end;
end;

procedure TFrmMain.ClearGroupInformation;
begin
  RadioGroupWorker.ItemIndex := 0;
  txtName.Text := '';
  txtApplicationFullPath.Text := '';
  txtTotalWorkers.Value := 1;
  txtMonitoringRate.Value := 30000;
  txtTimeoutKeepAlive.Value := 15000;
  RadioGroupBoost.ItemIndex := 1;
  txtBoostWorkers.Value := 0;
  txtStartTimeBoost.Time := StrToTime('00:00:00');
  txtEndTimeBoost.Time := StrToTime('00:00:00');
end;

procedure TFrmMain.ClearWorkersNode(const pWorkerGroupName : string);
var
  Node: TTreeNode;
begin
  for Node in TreeViewWorkersGroups.Items do
  begin
    if Node.Text = pWorkerGroupName then
    begin
      Node.DeleteChildren;
    end;
  end;
end;

procedure TFrmMain.CurrentWorkersHandler(pMessage: TJSONObject;
  var pProcessing: boolean);
var
  I, J : integer;
  WorkerGroup, Response : TJSONObject;
  WorkerGroups,Workers : TJSONArray;
  Worker : TWorker;
  RootNode : TTreeNode;
begin
  TreeViewWorkersGroups.Enabled := True;
  TabSheetConfiguration.Enabled := True;
  RootNode := TreeViewWorkersGroups.Items.GetFirstNode;
  RootNode.Text := StringReplace(RootNode.Text, '(Not Connected)', '(Connected)', [rfReplaceAll]);
  if Assigned(FWorkers) then
    FWorkers.Free;
  FWorkers := TObjectList<TWorker>.Create(True);
  Response := pMessage.GetValue<TJSONObject>('Response');
  WorkerGroups := Response.GetValue<TJSONArray>('WorkerGroups');
  for I := 0 to Pred(WorkerGroups.Count) do
  begin
    WorkerGroup := WorkerGroups.Items[i] as TJSONObject;
    Workers := WorkerGroup.GetValue<TJSONArray>('Workers');
    CheckSynchronizePending(WorkerGroup.GetValue<string>('Name'),
      StrToDateTime(WorkerGroup.GetValue<string>('LastSyncConfig')));
    ClearWorkersNode(WorkerGroup.GetValue<string>('Name'));
    for J := 0 to Pred(Workers.Count) do
    begin
      Worker := TWorker.Create;
      Worker.ApplicationName := ExtractFileName(WorkerGroup.GetValue<string>('ApplicationFullPath'));
      Worker.GroupName := WorkerGroup.GetValue<string>('Name');
      Worker.ProcessId := Workers.Items[J].GetValue<Cardinal>('ProcessId');
      Worker.LastKeepAlive := StrToDateTime(Workers.Items[J].GetValue<string>('LastKeepAlive'));
      FWorkers.Add(Worker);
    end;
  end;
  TThread.Queue(nil, procedure
  begin
    AssertWorkers;
  end);
  pProcessing := False;
end;

procedure TFrmMain.Edit1Click(Sender: TObject);
begin
  GroupBoxButtonsEdit.Visible := True;
  txtName.Enabled := False;
  PanelGroupInformation.Enabled := True;
  TreeViewWorkersGroups.Enabled := False;
end;

procedure TFrmMain.FormClose(Sender: TObject; var Action: TCloseAction);
begin
  if btnStopTrace.Enabled then
  begin
    MessageDlg('Please, stop the current Trace Online before quit the application!', mtError,
      [mbOK], 0);
    Action := TCloseAction.caNone;
  end
  else
  begin
    FZapMQWrapper.SafeStop;
    FZapMQWrapper.Free;
    if Assigned(FWorkerControlConfig) then
      FWorkerControlConfig.Free;
    if Assigned(FWorkers) then
      FWorkers.Free;
    Action := TCloseAction.caFree;
  end;
end;

procedure TFrmMain.FormCreate(Sender: TObject);
begin
  PageControl1.ActivePageIndex := 0;
  StatusBar1.Panels[2].Text := 'Service Status :' +  TServiceManager.GetServiceState;
  LoadConfigFile;
  FZapMQWrapper := TZapMQWrapper.Create(FWorkerControlConfig.ZapMQHost,
    FWorkerControlConfig.ZapMQPort);
  FZapMQWrapper.OnRPCExpired := RPCExpired;
  LoadCurrentWorkers;
end;

function TFrmMain.GetSynchronizePending(
  const pWorkerGroupName: string): boolean;
var
  WorkerGroup: TWorkerGroupConfig;
begin
  Result := True;
  for WorkerGroup in FWorkerControlConfig.WorkerGroupsConfig do
  begin
    if WorkerGroup.Name = pWorkerGroupName then
    begin
      Result := WorkerGroup.SynchronizePendind;
      Break;
    end;
  end;
end;

procedure TFrmMain.LoadConfigFile;
var
  WorkerGroup: TWorkerGroupConfig;
  WorkerGroupNode, RootNode : TTreeNode;
begin
  TreeViewWorkersGroups.Items.Clear;
  TreeViewWorkersGroups.Enabled := False;
  RootNode := TreeViewWorkersGroups.Items.Add(nil, 'Worker Control Service (Not Connected)');
  RootNode.Expand(True);
  FWorkerControlConfig := TConfig.FromFile('ConfigWorkers.json');
  StatusBar1.Panels[0].Text := 'ZapMQ Host :' + FWorkerControlConfig.ZapMQHost;
  StatusBar1.Panels[1].Text := 'ZapMQ Port :' + FWorkerControlConfig.ZapMQPort.ToString;
  for WorkerGroup in FWorkerControlConfig.WorkerGroupsConfig do
  begin
    WorkerGroupNode := TreeViewWorkersGroups.Items.AddChild(
        RootNode, WorkerGroup.Name);
    WorkerGroupNode.ImageIndex := 1;
    WorkerGroupNode.SelectedIndex := 1;
  end;
end;

procedure TFrmMain.LoadCurrentWorkers;
var
  Json : TJSONObject;
begin
  Json := TJSONObject.Create;
  try
    Json.AddPair('Message', 'CurrentWorkers');
    FZapMQWrapper.SendRPCMessage('WorkerControlAdmin', Json,
      CurrentWorkersHandler, 10000);
  finally
    Json.Free;
  end;
end;

procedure TFrmMain.LoadWorkGroupInformation(const pIndex: integer);
begin
  if FWorkerControlConfig.WorkerGroupsConfig.Items[pIndex].Enabled then
    RadioGroupWorker.ItemIndex := 0
  else
    RadioGroupWorker.ItemIndex := 1;
  lblGroupName.Caption := FWorkerControlConfig.WorkerGroupsConfig.Items[pIndex].Name;
  txtName.Text := FWorkerControlConfig.WorkerGroupsConfig.Items[pIndex].Name;
  txtApplicationFullPath.Text := FWorkerControlConfig.WorkerGroupsConfig.Items[pIndex].ApplicationFullPath;
  txtTotalWorkers.Value := FWorkerControlConfig.WorkerGroupsConfig.Items[pIndex].TotalWorkers;
  lblTotalWorkers.Caption := FWorkerControlConfig.WorkerGroupsConfig.Items[pIndex].TotalWorkers.ToString;
  txtMonitoringRate.Value := FWorkerControlConfig.WorkerGroupsConfig.Items[pIndex].MonitoringRate;
  txtTimeoutKeepAlive.Value := FWorkerControlConfig.WorkerGroupsConfig.Items[pIndex].TimeoutKeepAlive;
  if FWorkerControlConfig.WorkerGroupsConfig.Items[pIndex].Boost.Enabled then
    RadioGroupBoost.ItemIndex := 0
  else
    RadioGroupBoost.ItemIndex := 1;
  txtBoostWorkers.Value := FWorkerControlConfig.WorkerGroupsConfig.Items[pIndex].Boost.BoostWorkers;
  txtStartTimeBoost.Time := FWorkerControlConfig.WorkerGroupsConfig.Items[pIndex].Boost.StartTime;
  txtEndTimeBoost.Time := FWorkerControlConfig.WorkerGroupsConfig.Items[pIndex].Boost.EndTime;
end;

procedure TFrmMain.NewGroup1Click(Sender: TObject);
begin
  TreeViewWorkersGroups.Items.GetFirstNode.Selected := True;
  TreeViewWorkersGroups.Enabled := False;
  PageControl1.Visible := True;
  PanelGroupInformation.Enabled := True;
  GroupBoxButtonsEdit.Visible := True;
  txtName.Enabled := True;
  ClearGroupInformation;
end;

procedure TFrmMain.Quit1Click(Sender: TObject);
begin
  if btnStopTrace.Enabled then
  begin
    MessageDlg('Please, stop the current Trace Online before quit the application!', mtError,
      [mbOK], 0);
  end
  else
  begin
    FrmMain.Close;
  end;
end;

procedure TFrmMain.Refresh1Click(Sender: TObject);
begin
  LoadCurrentWorkers;
end;

procedure TFrmMain.ReloadConfigHandler(pMessage: TJSONObject;
  var pProcessing: boolean);
var
  RootNode : TTreeNode;
begin
  TreeViewWorkersGroups.Enabled := True;
  TabSheetConfiguration.Enabled := True;
  RootNode := TreeViewWorkersGroups.Items.GetFirstNode;
  RootNode.Text := StringReplace(RootNode.Text, '(Not Connected)', '(Connected)', [rfReplaceAll]);
  TreeViewWorkersGroups.Enabled := True;
  LoadCurrentWorkers;
end;

procedure TFrmMain.Remove1Click(Sender: TObject);
var
  Confirmation : integer;
  SelectedNode : TTreeNode;
  GroupConfig: TWorkerGroupConfig;
  Json : TJSONObject;
begin
  SelectedNode := TreeViewWorkersGroups.Selected;
  if SelectedNode.Level > 0 then
  begin
    while SelectedNode.Level <> 1 do
    begin
      SelectedNode := SelectedNode.GetPrev;
    end;
    Confirmation := MessageDlg('Are you sure you want to delete the group ' +
    TreeViewWorkersGroups.Selected.Text + '?', mtConfirmation, [mbYes, mbNo], 0, mbNo);
    if Confirmation = 6 then
    begin
      for GroupConfig in FWorkerControlConfig.WorkerGroupsConfig do
      begin
        if GroupConfig.Name = SelectedNode.Text then
        begin
          FWorkerControlConfig.WorkerGroupsConfig.Remove(GroupConfig);
          FWorkerControlConfig.ToFile('ConfigWorkers.json');
          SelectedNode.Delete;
          Json := TJSONObject.Create;
          try
            Json.AddPair('Message', 'ReloadConfig');
            FZapMQWrapper.SendRPCMessage('WorkerControlAdmin', Json, ReloadConfigHandler, 30000);
          finally
            Json.Free;
          end;
          Break;
        end;
      end;
      MessageDlg('Please restart the service for this change to take effect.',
        mtInformation, [mbOK], 0);
    end;
  end;
end;

procedure TFrmMain.RPCExpired(const pMessage: TZapJSONMessage);
var
  RootNode : TTreeNode;
  ProcessId : Cardinal;
  Worker: TWorker;
begin
  if pMessage.Body.GetValue<string>('message') = 'start trace' then
  begin
    ProcessId := pMessage.Body.GetValue<Cardinal>('ProcessId');
    for Worker in FWorkers do
    begin
      if Worker.ProcessId = ProcessId then
        Worker.WorkerNotResponding;
    end;
  end
  else
  begin
    TreeViewWorkersGroups.Enabled := False;
    TabSheetConfiguration.Enabled := False;
    RootNode := TreeViewWorkersGroups.Items.GetFirstNode;
    RootNode.Text := StringReplace(RootNode.Text, '(Connected)', '(Not Connected)', [rfReplaceAll]);
  end;
end;

procedure TFrmMain.SetLastSynchronize(const pWorkerGroupName: string);
var
  WorkerGroup: TWorkerGroupConfig;
begin
  for WorkerGroup in FWorkerControlConfig.WorkerGroupsConfig do
  begin
    if WorkerGroup.Name = pWorkerGroupName then
    begin
      WorkerGroup.LastSyncConfig := Now;
      Break;
    end;
  end;
end;

procedure TFrmMain.Start1Click(Sender: TObject);
var
  Timeout : integer;
begin
  TServiceManager.StartService;
  Timeout := 0;
  while (TServiceManager.GetServiceState <> 'SERVICE_RUNNING') or (Timeout <= 15) do
  begin
    StatusBar1.Panels[2].Text := 'Service Status :' +  TServiceManager.GetServiceState;
    Application.ProcessMessages;
    Inc(Timeout, 1);
  end;
  StatusBar1.Panels[2].Text := 'Service Status :' +  TServiceManager.GetServiceState;
end;

procedure TFrmMain.Stop2Click(Sender: TObject);
var
  Timeout : integer;
begin
  TServiceManager.StopService;
  Timeout := 0;
  while (TServiceManager.GetServiceState <> 'SERVICE_STOPPED') or (Timeout <= 15) do
  begin
    StatusBar1.Panels[2].Text := 'Service Status :' +  TServiceManager.GetServiceState;
    Application.ProcessMessages;
    Inc(Timeout, 1);
  end;
  StatusBar1.Panels[2].Text := 'Service Status :' +  TServiceManager.GetServiceState;
end;

procedure TFrmMain.TreeViewWorkersGroupsChange(Sender: TObject;
  Node: TTreeNode);
var
  NodeSelected : TTreeNode;
begin
  NodeSelected := TreeViewWorkersGroups.Selected;
  if NodeSelected.Level = 0 then
  begin
    PageControl1.Visible := False;
  end
  else if NodeSelected.Level > 0 then
  begin
    PageControl1.Visible := True;
    PageControl1.ActivePageIndex := 0;
    while NodeSelected.Level <> 1 do
    begin
      NodeSelected := NodeSelected.GetPrev;
    end;
    LoadWorkGroupInformation(NodeSelected.Index);
  end;
end;

end.
