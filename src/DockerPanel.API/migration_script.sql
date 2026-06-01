CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

CREATE TABLE "Users" (
    "Id" uuid NOT NULL,
    "Username" character varying(50) NOT NULL,
    "PasswordHash" character varying(255) NOT NULL,
    "Role" character varying(20) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    CONSTRAINT "PK_Users" PRIMARY KEY ("Id")
);

CREATE TABLE "DatabaseSchemas" (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "DbName" character varying(63) NOT NULL,
    "DbUser" character varying(63) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_DatabaseSchemas" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_DatabaseSchemas_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
);

CREATE TABLE "DnsRecords" (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "Type" character varying(10) NOT NULL,
    "Name" character varying(253) NOT NULL,
    "Value" text NOT NULL,
    "Ttl" integer NOT NULL DEFAULT 3600,
    "Proxied" boolean NOT NULL DEFAULT FALSE,
    "CloudflareRecordId" character varying(128),
    CONSTRAINT "PK_DnsRecords" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_DnsRecords_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
);

CREATE TABLE "MailAccounts" (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "EmailAddress" character varying(254) NOT NULL,
    "QuotaBytes" bigint NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_MailAccounts" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_MailAccounts_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
);

CREATE TABLE "Projects" (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "DockerContainerId" character varying(128),
    "Name" character varying(64) NOT NULL,
    "Type" character varying(20) NOT NULL,
    "ImageOrPath" character varying(255) NOT NULL,
    "MemoryLimitBytes" bigint NOT NULL,
    "CpuCount" double precision NOT NULL,
    "InternalPort" integer NOT NULL,
    "Status" character varying(20) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_Projects" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Projects_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
);

CREATE TABLE "Subdomains" (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "ProjectId" uuid NOT NULL,
    "SubdomainName" character varying(63) NOT NULL,
    "DomainName" character varying(253) NOT NULL,
    "SslEnabled" boolean NOT NULL DEFAULT TRUE,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_Subdomains" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Subdomains_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_Subdomains_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX "IX_DatabaseSchemas_DbName" ON "DatabaseSchemas" ("DbName");

CREATE UNIQUE INDEX "IX_DatabaseSchemas_DbUser" ON "DatabaseSchemas" ("DbUser");

CREATE INDEX "IX_DatabaseSchemas_UserId" ON "DatabaseSchemas" ("UserId");

CREATE INDEX "IX_DnsRecords_UserId" ON "DnsRecords" ("UserId");

CREATE UNIQUE INDEX "IX_MailAccounts_EmailAddress" ON "MailAccounts" ("EmailAddress");

CREATE INDEX "IX_MailAccounts_UserId" ON "MailAccounts" ("UserId");

CREATE UNIQUE INDEX "IX_Projects_Name" ON "Projects" ("Name");

CREATE INDEX "IX_Projects_UserId" ON "Projects" ("UserId");

CREATE INDEX "IX_Subdomains_ProjectId" ON "Subdomains" ("ProjectId");

CREATE UNIQUE INDEX "IX_Subdomains_SubdomainName_DomainName" ON "Subdomains" ("SubdomainName", "DomainName");

CREATE INDEX "IX_Subdomains_UserId" ON "Subdomains" ("UserId");

CREATE UNIQUE INDEX "IX_Users_Username" ON "Users" ("Username");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260518222500_AddUsersTable', '8.0.8');

COMMIT;

