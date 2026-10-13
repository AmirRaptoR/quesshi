-- Orleans 10.2.2 ADO.NET PostgreSQL main and clustering schema.
-- The provider SQL below is vendored from the pinned upstream scripts without
-- altering its membership protocol.  Only schema selection and grants are
-- Quesshi additions.
-- Source commit: a575888773a3b01b079c9c3720bb1553c3169997
-- https://github.com/dotnet/orleans/blob/a575888773a3b01b079c9c3720bb1553c3169997/src/AdoNet/Orleans.Clustering.AdoNet/PostgreSQL-Clustering.sql
-- The required CleanupDefunctSiloEntriesKey is from the matching provider migration stream:
-- https://github.com/dotnet/orleans/blob/a575888773a3b01b079c9c3720bb1553c3169997/src/AdoNet/Orleans.Clustering.AdoNet/Migrations/PostgreSQL-Clustering-3.7.0.sql
create schema orleans authorization quesshi_owner;
set local search_path = orleans;

-- PostgreSQL-Main.sql
create table OrleansQuery
(
    QueryKey varchar(64) NOT NULL,
    QueryText varchar(8000) NOT NULL,

    CONSTRAINT OrleansQuery_Key PRIMARY KEY(QueryKey)
);

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
create function update_i_am_alive_time(
    deployment_id OrleansMembershipTable.DeploymentId%TYPE,
    address_arg OrleansMembershipTable.Address%TYPE,
    port_arg OrleansMembershipTable.Port%TYPE,
    generation_arg OrleansMembershipTable.Generation%TYPE,
    i_am_alive_time OrleansMembershipTable.IAmAliveTime%TYPE)
returns void as
$func$
begin
    update OrleansMembershipTable as d set IAmAliveTime = i_am_alive_time
    where d.DeploymentId = deployment_id and deployment_id is not null
      and d.Address = address_arg and address_arg is not null
      and d.Port = port_arg and port_arg is not null
      and d.Generation = generation_arg and generation_arg is not null;
end
$func$ language plpgsql;

create function insert_membership_version(
    DeploymentIdArg OrleansMembershipTable.DeploymentId%TYPE)
returns table(row_count integer) as
$func$
declare RowCountVar int := 0;
begin
    begin
        insert into OrleansMembershipVersionTable (DeploymentId)
        select DeploymentIdArg
        on conflict (DeploymentId) do nothing;
        get diagnostics RowCountVar = row_count;
        assert RowCountVar <> 0, 'no rows affected, rollback';
        return query select RowCountVar;
    exception when assert_failure then
        return query select RowCountVar;
    end;
end
$func$ language plpgsql;

create function insert_membership(
    DeploymentIdArg OrleansMembershipTable.DeploymentId%TYPE,
    AddressArg OrleansMembershipTable.Address%TYPE,
    PortArg OrleansMembershipTable.Port%TYPE,
    GenerationArg OrleansMembershipTable.Generation%TYPE,
    SiloNameArg OrleansMembershipTable.SiloName%TYPE,
    HostNameArg OrleansMembershipTable.HostName%TYPE,
    StatusArg OrleansMembershipTable.Status%TYPE,
    ProxyPortArg OrleansMembershipTable.ProxyPort%TYPE,
    StartTimeArg OrleansMembershipTable.StartTime%TYPE,
    IAmAliveTimeArg OrleansMembershipTable.IAmAliveTime%TYPE,
    VersionArg OrleansMembershipVersionTable.Version%TYPE)
returns table(row_count integer) as
$func$
declare RowCountVar int := 0;
begin
    begin
        insert into OrleansMembershipTable
            (DeploymentId, Address, Port, Generation, SiloName, HostName, Status, ProxyPort, StartTime, IAmAliveTime)
        select DeploymentIdArg, AddressArg, PortArg, GenerationArg, SiloNameArg, HostNameArg, StatusArg, ProxyPortArg, StartTimeArg, IAmAliveTimeArg
        on conflict (DeploymentId, Address, Port, Generation) do nothing;
        get diagnostics RowCountVar = row_count;
        update OrleansMembershipVersionTable
        set Timestamp = now(), Version = Version + 1
        where DeploymentId = DeploymentIdArg and DeploymentIdArg is not null
          and Version = VersionArg and VersionArg is not null
          and RowCountVar > 0;
        get diagnostics RowCountVar = row_count;
        assert RowCountVar <> 0, 'no rows affected, rollback';
        return query select RowCountVar;
    exception when assert_failure then
        return query select RowCountVar;
    end;
end
$func$ language plpgsql;

create function update_membership(
    DeploymentIdArg OrleansMembershipTable.DeploymentId%TYPE,
    AddressArg OrleansMembershipTable.Address%TYPE,
    PortArg OrleansMembershipTable.Port%TYPE,
    GenerationArg OrleansMembershipTable.Generation%TYPE,
    StatusArg OrleansMembershipTable.Status%TYPE,
    SuspectTimesArg OrleansMembershipTable.SuspectTimes%TYPE,
    IAmAliveTimeArg OrleansMembershipTable.IAmAliveTime%TYPE,
    VersionArg OrleansMembershipVersionTable.Version%TYPE)
returns table(row_count integer) as
$func$
declare RowCountVar int := 0;
begin
    begin
        update OrleansMembershipVersionTable
        set Timestamp = now(), Version = Version + 1
        where DeploymentId = DeploymentIdArg and DeploymentIdArg is not null
          and Version = VersionArg and VersionArg is not null;
        get diagnostics RowCountVar = row_count;
        update OrleansMembershipTable
        set Status = StatusArg, SuspectTimes = SuspectTimesArg, IAmAliveTime = IAmAliveTimeArg
        where DeploymentId = DeploymentIdArg and DeploymentIdArg is not null
          and Address = AddressArg and AddressArg is not null
          and Port = PortArg and PortArg is not null
          and Generation = GenerationArg and GenerationArg is not null
          and RowCountVar > 0;
        get diagnostics RowCountVar = row_count;
        assert RowCountVar <> 0, 'no rows affected, rollback';
        return query select RowCountVar;
    exception when assert_failure then
        return query select RowCountVar;
    end;
end
$func$ language plpgsql;
insert into OrleansQuery(QueryKey, QueryText) values
('UpdateIAmAlivetimeKey', $query$
    SELECT * from update_i_am_alive_time(@DeploymentId, @Address, @Port, @Generation, @IAmAliveTime);
$query$),
('InsertMembershipVersionKey', $query$
    SELECT * FROM insert_membership_version(@DeploymentId);
$query$),
('InsertMembershipKey', $query$
    SELECT * FROM insert_membership(@DeploymentId, @Address, @Port, @Generation, @SiloName, @HostName, @Status, @ProxyPort, @StartTime, @IAmAliveTime, @Version);
$query$),
('UpdateMembershipKey', $query$
    SELECT * FROM update_membership(@DeploymentId, @Address, @Port, @Generation, @Status, @SuspectTimes, @IAmAliveTime, @Version);
$query$),
('MembershipReadRowKey', $query$
    SELECT v.DeploymentId, m.Address, m.Port, m.Generation, m.SiloName, m.HostName, m.Status, m.ProxyPort, m.SuspectTimes, m.StartTime, m.IAmAliveTime, v.Version
    FROM OrleansMembershipVersionTable v LEFT OUTER JOIN OrleansMembershipTable m ON v.DeploymentId = m.DeploymentId
        AND Address = @Address AND @Address IS NOT NULL
        AND Port = @Port AND @Port IS NOT NULL
        AND Generation = @Generation AND @Generation IS NOT NULL
    WHERE v.DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
$query$),
('MembershipReadAllKey', $query$
    SELECT v.DeploymentId, m.Address, m.Port, m.Generation, m.SiloName, m.HostName, m.Status, m.ProxyPort, m.SuspectTimes, m.StartTime, m.IAmAliveTime, v.Version
    FROM OrleansMembershipVersionTable v LEFT OUTER JOIN OrleansMembershipTable m ON v.DeploymentId = m.DeploymentId
    WHERE v.DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
$query$),
('DeleteMembershipTableEntriesKey', $query$
    DELETE FROM OrleansMembershipTable WHERE DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
    DELETE FROM OrleansMembershipVersionTable WHERE DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
$query$),
('CleanupDefunctSiloEntriesKey', $query$
    DELETE FROM OrleansMembershipTable WHERE DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL AND IAmAliveTime < @IAmAliveTime AND Status != 3;
$query$),
('GatewaysQueryKey', $query$
    SELECT Address, ProxyPort, Generation FROM OrleansMembershipTable
    WHERE DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL AND Status = @Status AND @Status IS NOT NULL AND ProxyPort > 0;
$query$);

revoke all on schema orleans from public, quesshi_app;
revoke all on all tables in schema orleans from public, quesshi_app;
revoke all on all functions in schema orleans from public, quesshi_app;
grant usage on schema orleans to quesshi_system;
grant select on orleans.OrleansQuery to quesshi_system;
grant select, insert, update, delete on orleans.OrleansMembershipVersionTable, orleans.OrleansMembershipTable to quesshi_system;
grant execute on all functions in schema orleans to quesshi_system;
grant select on public.schema_migrations to quesshi_system;
