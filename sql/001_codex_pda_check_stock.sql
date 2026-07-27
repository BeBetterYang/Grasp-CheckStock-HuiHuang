USE [hh2j1332]
GO

IF OBJECT_ID('dbo.CodexPdaCheckedCountMap', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CodexPdaCheckedCountMap
    (
        MapID int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        SubmitDetailID int NOT NULL,
        CheckedCountID int NOT NULL,
        UpdateTag int NOT NULL CONSTRAINT DF_CodexPdaMap_UpdateTag DEFAULT(0),
        CreatedAt datetime NOT NULL CONSTRAINT DF_CodexPdaCheckedCountMap_CreatedAt DEFAULT(GETDATE())
    )
END
GO

IF COL_LENGTH('dbo.CodexPdaCheckedCountMap', 'UpdateTag') IS NULL
    ALTER TABLE dbo.CodexPdaCheckedCountMap ADD UpdateTag int NOT NULL CONSTRAINT DF_CodexPdaMap_UpdateTag DEFAULT(0)
GO

IF OBJECT_ID('dbo.CodexPdaCheckSubmitDetail', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CodexPdaCheckSubmitDetail
    (
        DetailID int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        SubmitID int NOT NULL,
        PTypeID varchar(50) NOT NULL,
        UnitOrdid varchar(21) NOT NULL CONSTRAINT DF_CodexPdaDetail_UnitOrdid DEFAULT('0'),
        UnitName varchar(20) NOT NULL CONSTRAINT DF_CodexPdaDetail_UnitName DEFAULT(''),
        UnitRate numeric(22,10) NOT NULL CONSTRAINT DF_CodexPdaDetail_UnitRate DEFAULT(1),
        GoodsBatchID varchar(50) NOT NULL CONSTRAINT DF_CodexPdaDetail_GoodsBatchID DEFAULT(''),
        GoodsOrderID int NOT NULL CONSTRAINT DF_CodexPdaDetail_GoodsOrderID DEFAULT(0),
        JobNumber varchar(50) NOT NULL CONSTRAINT DF_CodexPdaDetail_JobNumber DEFAULT(''),
        OutFactoryDate varchar(13) NOT NULL CONSTRAINT DF_CodexPdaDetail_OutFactoryDate DEFAULT(''),
        UsefulEndDate varchar(10) NOT NULL CONSTRAINT DF_CodexPdaDetail_UsefulEndDate DEFAULT(''),
        StockQty numeric(22,10) NOT NULL CONSTRAINT DF_CodexPdaDetail_StockQty DEFAULT(0),
        StockPgHolInqty numeric(22,10) NOT NULL CONSTRAINT DF_CodexPdaDetail_StockPgHolInqty DEFAULT(0),
        CheckedQty numeric(22,10) NOT NULL CONSTRAINT DF_CodexPdaDetail_CheckedQty DEFAULT(0),
        CheckedBaseQty numeric(22,10) NOT NULL CONSTRAINT DF_CodexPdaDetail_CheckedBaseQty DEFAULT(0),
        ProfitQty numeric(22,10) NOT NULL CONSTRAINT DF_CodexPdaDetail_ProfitQty DEFAULT(0),
        IsNew bit NOT NULL CONSTRAINT DF_CodexPdaDetail_IsNew DEFAULT(0),
        CreatedAt datetime NOT NULL CONSTRAINT DF_CodexPdaDetail_CreatedAt DEFAULT(GETDATE())
    )
END
GO

IF OBJECT_ID('dbo.CodexPdaCheckSubmit', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CodexPdaCheckSubmit
    (
        SubmitID int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        HeaderID int NOT NULL,
        KTypeID varchar(25) NOT NULL,
        CheckDate varchar(10) NOT NULL,
        ETypeID varchar(25) NOT NULL,
        SubmittedAt datetime NOT NULL CONSTRAINT DF_CodexPdaSubmit_SubmittedAt DEFAULT(GETDATE()),
        ItemCount int NOT NULL CONSTRAINT DF_CodexPdaSubmit_ItemCount DEFAULT(0),
        BatchCount int NOT NULL CONSTRAINT DF_CodexPdaSubmit_BatchCount DEFAULT(0)
    )
END
GO

IF OBJECT_ID('dbo.CodexPdaCheckHeader', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CodexPdaCheckHeader
    (
        HeaderID int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        KTypeID varchar(25) NOT NULL,
        CheckDate varchar(10) NOT NULL,
        ETypeID varchar(25) NOT NULL,
        Status varchar(20) NOT NULL CONSTRAINT DF_CodexPdaHeader_Status DEFAULT('Submitted'),
        Remark varchar(200) NOT NULL CONSTRAINT DF_CodexPdaHeader_Remark DEFAULT(''),
        CreatedAt datetime NOT NULL CONSTRAINT DF_CodexPdaHeader_CreatedAt DEFAULT(GETDATE())
    )
END
GO

IF OBJECT_ID('dbo.CodexPda_GetWarehouseCheck', 'P') IS NOT NULL
    DROP PROCEDURE dbo.CodexPda_GetWarehouseCheck
GO

CREATE PROCEDURE dbo.CodexPda_GetWarehouseCheck
    @KTypeID varchar(25)
AS
BEGIN
    SET NOCOUNT ON

    DECLARE @CheckDate varchar(10)
    DECLARE @HeaderID int
    DECLARE @CheckedMode int
    DECLARE @UpdateTag int

    SELECT TOP 1
        @CheckDate = cc.Date,
        @HeaderID = cc.ID,
        @CheckedMode = cc.CHECKEDMODE,
        @UpdateTag = cc.UpdateTag
    FROM dbo.CheckedCount cc
    WHERE cc.KTypeID = @KTypeID
      AND ISNULL(cc.PTypeID, '') = ''
    ORDER BY cc.Date DESC, cc.ID DESC

    IF @CheckDate IS NULL
        RETURN

    SELECT
        @KTypeID AS KTypeID,
        ISNULL(s.kfullname, @KTypeID) AS kfullname,
        @CheckDate AS Date,
        @CheckedMode AS CHECKEDMODE,
        @UpdateTag AS UpdateTag,
        @HeaderID AS ID
    FROM dbo.Stock s
    WHERE s.ktypeid = @KTypeID
    UNION ALL
    SELECT
        @KTypeID AS KTypeID,
        @KTypeID AS kfullname,
        @CheckDate AS Date,
        @CheckedMode AS CHECKEDMODE,
        @UpdateTag AS UpdateTag,
        @HeaderID AS ID
    WHERE NOT EXISTS (SELECT 1 FROM dbo.Stock s WHERE s.ktypeid = @KTypeID)
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CodexPdaCheckSubmit') AND name = 'IX_CodexPdaCheckSubmit_Date')
    CREATE INDEX IX_CodexPdaCheckSubmit_Date ON dbo.CodexPdaCheckSubmit(KTypeID, CheckDate, SubmittedAt)
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CodexPdaCheckSubmitDetail') AND name = 'IX_CodexPdaCheckSubmitDetail_Submit')
    CREATE INDEX IX_CodexPdaCheckSubmitDetail_Submit ON dbo.CodexPdaCheckSubmitDetail(SubmitID, PTypeID)
GO
