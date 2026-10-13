-- Orleans 10.2.2 ADO.NET PostgreSQL main and clustering schema.
-- Source commit: a575888773a3b01b079c9c3720bb1553c3169997
-- https://github.com/dotnet/orleans/blob/a575888773a3b01b079c9c3720bb1553c3169997/src/AdoNet/Orleans.Clustering.AdoNet/PostgreSQL-Clustering.sql
-- The required CleanupDefunctSiloEntriesKey is from the matching provider migration stream:
-- https://github.com/dotnet/orleans/blob/a575888773a3b01b079c9c3720bb1553c3169997/src/AdoNet/Orleans.Clustering.AdoNet/Migrations/PostgreSQL-Clustering-3.7.0.sql
create schema orleans authorization quesshi_owner;
set local search_path = orleans;

-- PostgreSQL-Main.sql
create table OrleansQuery (QueryKey varchar(150) primary key, QueryText text not null);

-- PostgreSQL-Clustering.sql
create table OrleansMembershipVersionTable (
    DeploymentId varchar(150) not null primary key,
    Timestamp timestamptz(3) not null default now(),
    Version integer not null default 0
);
create table OrleansMembershipTable (
    DeploymentId varchar(150) not null,
    Address varchar(45) not null,
    Port integer not null,
    Generation integer not null,
    SiloName varchar(150) not null,
    HostName varchar(150) not null,
    Status integer not null,
    ProxyPort integer null,
    SuspectTimes varchar(8000) null,
    StartTime timestamptz(3) not null,
    IAmAliveTime timestamptz(3) not null,
    constraint PK_MembershipTable_DeploymentId primary key(DeploymentId, Address, Port, Generation),
    constraint FK_MembershipTable_MembershipVersionTable_DeploymentId foreign key (DeploymentId) references OrleansMembershipVersionTable (DeploymentId)
);
create function update_i_am_alive_time(deployment_id varchar, address_arg varchar, port_arg integer, generation_arg integer, i_am_alive_time timestamptz)
returns void language plpgsql as $$ begin update OrleansMembershipTable set IAmAliveTime = i_am_alive_time where DeploymentId = deployment_id and Address = address_arg and Port = port_arg and Generation = generation_arg; end $$;
create function insert_membership_version(deployment_id_arg varchar) returns table(row_count integer) language plpgsql as $$ begin
  insert into OrleansMembershipVersionTable(DeploymentId) values (deployment_id_arg) on conflict do nothing;
  get diagnostics row_count = row_count; return query select row_count;
end $$;
create function insert_membership(deployment_id_arg varchar, address_arg varchar, port_arg integer, generation_arg integer, silo_name_arg varchar, host_name_arg varchar, status_arg integer, proxy_port_arg integer, start_time_arg timestamptz, alive_time_arg timestamptz, version_arg integer) returns table(row_count integer) language plpgsql as $$ begin
  insert into OrleansMembershipTable(DeploymentId,Address,Port,Generation,SiloName,HostName,Status,ProxyPort,StartTime,IAmAliveTime) values(deployment_id_arg,address_arg,port_arg,generation_arg,silo_name_arg,host_name_arg,status_arg,proxy_port_arg,start_time_arg,alive_time_arg) on conflict do nothing;
  get diagnostics row_count = row_count; if row_count > 0 then update OrleansMembershipVersionTable set Timestamp=now(), Version=Version+1 where DeploymentId=deployment_id_arg and Version=version_arg; get diagnostics row_count = row_count; end if; return query select row_count;
end $$;
create function update_membership(deployment_id_arg varchar, address_arg varchar, port_arg integer, generation_arg integer, status_arg integer, suspect_times_arg varchar, alive_time_arg timestamptz, version_arg integer) returns table(row_count integer) language plpgsql as $$ begin
  update OrleansMembershipVersionTable set Timestamp=now(), Version=Version+1 where DeploymentId=deployment_id_arg and Version=version_arg; get diagnostics row_count = row_count;
  if row_count > 0 then update OrleansMembershipTable set Status=status_arg,SuspectTimes=suspect_times_arg,IAmAliveTime=alive_time_arg where DeploymentId=deployment_id_arg and Address=address_arg and Port=port_arg and Generation=generation_arg; get diagnostics row_count = row_count; end if; return query select row_count;
end $$;
insert into OrleansQuery(QueryKey, QueryText) values
('UpdateIAmAlivetimeKey', 'select * from update_i_am_alive_time(@DeploymentId,@Address,@Port,@Generation,@IAmAliveTime);'),
('InsertMembershipVersionKey', 'select * from insert_membership_version(@DeploymentId);'),
('InsertMembershipKey', 'select * from insert_membership(@DeploymentId,@Address,@Port,@Generation,@SiloName,@HostName,@Status,@ProxyPort,@StartTime,@IAmAliveTime,@Version);'),
('UpdateMembershipKey', 'select * from update_membership(@DeploymentId,@Address,@Port,@Generation,@Status,@SuspectTimes,@IAmAliveTime,@Version);'),
('MembershipReadRowKey', 'select v.DeploymentId,m.Address,m.Port,m.Generation,m.SiloName,m.HostName,m.Status,m.ProxyPort,m.SuspectTimes,m.StartTime,m.IAmAliveTime,v.Version from OrleansMembershipVersionTable v left join OrleansMembershipTable m on v.DeploymentId=m.DeploymentId and m.Address=@Address and m.Port=@Port and m.Generation=@Generation where v.DeploymentId=@DeploymentId;'),
('MembershipReadAllKey', 'select v.DeploymentId,m.Address,m.Port,m.Generation,m.SiloName,m.HostName,m.Status,m.ProxyPort,m.SuspectTimes,m.StartTime,m.IAmAliveTime,v.Version from OrleansMembershipVersionTable v left join OrleansMembershipTable m on v.DeploymentId=m.DeploymentId where v.DeploymentId=@DeploymentId;'),
('DeleteMembershipTableEntriesKey', 'delete from OrleansMembershipTable where DeploymentId=@DeploymentId; delete from OrleansMembershipVersionTable where DeploymentId=@DeploymentId;'),
('CleanupDefunctSiloEntriesKey', 'delete from OrleansMembershipTable where DeploymentId=@DeploymentId and IAmAliveTime < @IAmAliveTime and Status != 3;'),
('GatewaysQueryKey', 'select Address,ProxyPort,Generation from OrleansMembershipTable where DeploymentId=@DeploymentId and Status=@Status and ProxyPort > 0;');

revoke all on schema orleans from public, quesshi_app;
revoke all on all tables in schema orleans from public, quesshi_app;
revoke all on all functions in schema orleans from public, quesshi_app;
grant usage on schema orleans to quesshi_system;
grant select, insert, update, delete on all tables in schema orleans to quesshi_system;
grant execute on all functions in schema orleans to quesshi_system;
grant select on public.schema_migrations to quesshi_system;
