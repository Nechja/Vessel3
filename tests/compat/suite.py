import os
import sys
import hashlib
import uuid
import datetime
import base64
import time
import threading
import traceback
import urllib.request
import urllib.error
from concurrent.futures import ThreadPoolExecutor

import boto3
from botocore.client import Config
from botocore.exceptions import ClientError

ENDPOINT = os.environ.get("VESSEL3_ENDPOINT", "http://127.0.0.1:9000")
AK = os.environ.get("VESSEL3_ACCESS_KEY", "AKIATEST")
SK = os.environ.get("VESSEL3_SECRET_KEY", "secretkey1234567890")
REGION = os.environ.get("VESSEL3_REGION", "us-east-1")
VERBOSE = bool(int(os.environ.get("VERBOSE", "0") or "0"))

os.environ.setdefault("AWS_REQUEST_CHECKSUM_CALCULATION", "WHEN_REQUIRED")
os.environ.setdefault("AWS_RESPONSE_CHECKSUM_VALIDATION", "WHEN_REQUIRED")

PASS = []
FAIL = []
SECTION = ""


def section(name):
    global SECTION
    SECTION = name
    print(f"\n=== {name} ===")


def test(name):
    def deco(fn):
        full = f"[{SECTION}] {name}"
        try:
            fn()
            PASS.append(full)
            print(f"  PASS  {name}")
        except AssertionError as e:
            FAIL.append((full, f"assert: {e}"))
            print(f"  FAIL  {name}: assert: {e}")
            if VERBOSE:
                traceback.print_exc(limit=3)
        except Exception as e:
            FAIL.append((full, repr(e)))
            print(f"  FAIL  {name}: {e}")
            if VERBOSE:
                traceback.print_exc(limit=3)
        return fn
    return deco


def mk_client(**overrides):
    cfg = {
        "endpoint_url": ENDPOINT,
        "aws_access_key_id": AK,
        "aws_secret_access_key": SK,
        "region_name": REGION,
        "config": Config(
            signature_version="s3v4",
            s3={"addressing_style": "path"},
            retries={"max_attempts": 2},
        ),
    }
    cfg.update(overrides)
    return boto3.client("s3", **cfg)


c = mk_client()
B = f"v3suite-{uuid.uuid4().hex[:8]}"
BV = f"v3ver-{uuid.uuid4().hex[:8]}"


def md5(b):
    return hashlib.md5(b).hexdigest()


def sha256(b):
    return hashlib.sha256(b).hexdigest()


section("Bucket lifecycle")


@test("create_bucket")
def _():
    c.create_bucket(Bucket=B)


@test("head_bucket_exists")
def _():
    c.head_bucket(Bucket=B)


@test("head_bucket_missing_404")
def _():
    try:
        c.head_bucket(Bucket=f"missing-{uuid.uuid4().hex}")
        raise AssertionError("expected 404")
    except ClientError as e:
        assert e.response["ResponseMetadata"]["HTTPStatusCode"] == 404


@test("invalid_bucket_name_uppercase_rejected")
def _():
    try:
        c.create_bucket(Bucket="HasUpperCase")
        raise AssertionError("expected validation error")
    except ClientError as e:
        code = e.response["Error"]["Code"]
        assert code in ("InvalidBucketName", "InvalidRequest", "InvalidPath"), code


@test("invalid_bucket_name_too_short")
def _():
    try:
        c.create_bucket(Bucket="ab")
        raise AssertionError("expected validation error")
    except ClientError:
        pass


@test("invalid_bucket_name_underscore")
def _():
    try:
        c.create_bucket(Bucket="has_underscore")
        raise AssertionError("expected InvalidBucketName")
    except ClientError:
        pass


@test("list_buckets_returns_owner_and_creation_date")
def _():
    r = c.list_buckets()
    assert "Buckets" in r
    found = [b for b in r["Buckets"] if b["Name"] == B]
    assert found, [b["Name"] for b in r["Buckets"]]
    assert "CreationDate" in found[0]


section("Object basic CRUD")


@test("put_zero_byte_object")
def _():
    r = c.put_object(Bucket=B, Key="empty", Body=b"")
    assert r["ETag"].strip('"') == md5(b"")
    g = c.get_object(Bucket=B, Key="empty")
    assert g["Body"].read() == b""
    assert g["ContentLength"] == 0


@test("put_get_small_text")
def _():
    body = b"hello vessel3\n"
    c.put_object(Bucket=B, Key="hello.txt", Body=body, ContentType="text/plain")
    g = c.get_object(Bucket=B, Key="hello.txt")
    assert g["Body"].read() == body
    assert g["ContentType"] == "text/plain"


@test("put_get_binary_random")
def _():
    body = os.urandom(64 * 1024)
    c.put_object(Bucket=B, Key="rand.bin", Body=body)
    assert c.get_object(Bucket=B, Key="rand.bin")["Body"].read() == body


@test("overwrite_object")
def _():
    c.put_object(Bucket=B, Key="overw", Body=b"v1")
    c.put_object(Bucket=B, Key="overw", Body=b"v2-longer")
    assert c.get_object(Bucket=B, Key="overw")["Body"].read() == b"v2-longer"


@test("head_object_metadata_and_size")
def _():
    h = c.head_object(Bucket=B, Key="hello.txt")
    assert h["ContentLength"] == len(b"hello vessel3\n")
    assert "ETag" in h
    assert "LastModified" in h


@test("delete_then_get_404")
def _():
    c.put_object(Bucket=B, Key="del-me", Body=b"x")
    c.delete_object(Bucket=B, Key="del-me")
    try:
        c.get_object(Bucket=B, Key="del-me")
        raise AssertionError("expected 404")
    except ClientError as e:
        assert e.response["ResponseMetadata"]["HTTPStatusCode"] == 404


@test("delete_missing_is_idempotent_2xx")
def _():
    r = c.delete_object(Bucket=B, Key="never-existed")
    code = r["ResponseMetadata"]["HTTPStatusCode"]
    assert code in (200, 204), code


section("Metadata and headers")


@test("user_metadata_roundtrip")
def _():
    c.put_object(
        Bucket=B,
        Key="meta.txt",
        Body=b"x",
        Metadata={"author": "kayla", "purpose": "test"},
    )
    h = c.head_object(Bucket=B, Key="meta.txt")
    meta = {k.lower(): v for k, v in h.get("Metadata", {}).items()}
    assert meta.get("author") == "kayla", meta
    assert meta.get("purpose") == "test", meta


@test("content_disposition_and_language_preserved")
def _():
    c.put_object(
        Bucket=B,
        Key="cd.txt",
        Body=b"x",
        ContentDisposition='attachment; filename="weird name.txt"',
        ContentLanguage="en-US",
        CacheControl="max-age=3600",
        ContentEncoding="identity",
    )
    h = c.head_object(Bucket=B, Key="cd.txt")
    assert h.get("ContentDisposition", "").startswith("attachment"), h.get("ContentDisposition")
    assert h.get("ContentLanguage") == "en-US"
    assert h.get("CacheControl") == "max-age=3600"
    assert h.get("ContentEncoding") == "identity"


@test("expires_header_preserved")
def _():
    c.put_object(
        Bucket=B,
        Key="exp.txt",
        Body=b"x",
        Expires=datetime.datetime(2099, 10, 21, 7, 28, 0, tzinfo=datetime.timezone.utc),
    )
    h = c.head_object(Bucket=B, Key="exp.txt")
    assert "Expires" in h or "ExpiresString" in h, list(h.keys())


section("Special key characters")

SPECIAL_KEYS = [
    "with spaces.txt",
    "unicode/中文/файл.txt",
    "plus+sign",
    "ampersand&here",
    "percent%encoded",
    "question?mark",
    "hash#mark",
    "equals=sign",
    "parens(and)brackets[and]",
    "quote'single",
    "slash/separated/deep/path",
    "double  spaces",
    "trailing.slash/",
    "emoji-😀-key",
]
for sk in SPECIAL_KEYS:
    @test(f"special_key: {sk!r}")
    def _(k=sk):
        body = f"body-of-{k}".encode("utf-8", "replace")
        c.put_object(Bucket=B, Key=k, Body=body)
        g = c.get_object(Bucket=B, Key=k)
        assert g["Body"].read() == body


@test("long_key_1020_chars")
def _():
    key = "long/" + ("x" * 1015)
    c.put_object(Bucket=B, Key=key, Body=b"k")
    assert c.head_object(Bucket=B, Key=key)["ContentLength"] == 1


section("Listing")

LIST_BUCKET = f"v3list-{uuid.uuid4().hex[:8]}"
c.create_bucket(Bucket=LIST_BUCKET)
for k in ["a", "b", "c", "dir1/x", "dir1/y", "dir1/z", "dir2/sub/m", "dir2/sub/n"]:
    c.put_object(Bucket=LIST_BUCKET, Key=k, Body=b"x")


@test("list_v1_basic")
def _():
    r = c.list_objects(Bucket=LIST_BUCKET)
    keys = [o["Key"] for o in r.get("Contents", [])]
    assert len(keys) == 8, keys


@test("list_v1_with_marker")
def _():
    r = c.list_objects(Bucket=LIST_BUCKET, Marker="b")
    keys = [o["Key"] for o in r.get("Contents", [])]
    assert "b" not in keys and "a" not in keys, keys


@test("list_v2_basic")
def _():
    r = c.list_objects_v2(Bucket=LIST_BUCKET)
    keys = [o["Key"] for o in r.get("Contents", [])]
    assert len(keys) == 8


@test("list_v2_prefix")
def _():
    r = c.list_objects_v2(Bucket=LIST_BUCKET, Prefix="dir1/")
    keys = [o["Key"] for o in r.get("Contents", [])]
    assert set(keys) == {"dir1/x", "dir1/y", "dir1/z"}, keys


@test("list_v2_delimiter_common_prefixes")
def _():
    r = c.list_objects_v2(Bucket=LIST_BUCKET, Delimiter="/")
    cps = [p["Prefix"] for p in r.get("CommonPrefixes", [])]
    keys = [o["Key"] for o in r.get("Contents", [])]
    assert set(cps) == {"dir1/", "dir2/"}, cps
    assert set(keys) == {"a", "b", "c"}, keys


@test("list_v2_prefix_plus_delimiter")
def _():
    r = c.list_objects_v2(Bucket=LIST_BUCKET, Prefix="dir2/", Delimiter="/")
    cps = [p["Prefix"] for p in r.get("CommonPrefixes", [])]
    assert cps == ["dir2/sub/"], cps


@test("list_v2_max_keys_1")
def _():
    r = c.list_objects_v2(Bucket=LIST_BUCKET, MaxKeys=1)
    assert len(r.get("Contents", [])) == 1
    assert r.get("IsTruncated") is True
    assert r.get("NextContinuationToken")


@test("list_v2_pagination_full_walk")
def _():
    seen = []
    token = None
    while True:
        kw = {"Bucket": LIST_BUCKET, "MaxKeys": 3}
        if token:
            kw["ContinuationToken"] = token
        r = c.list_objects_v2(**kw)
        seen += [o["Key"] for o in r.get("Contents", [])]
        if not r.get("IsTruncated"):
            break
        token = r["NextContinuationToken"]
    assert len(seen) == 8, seen


@test("list_v2_with_encoding_type_url_returns_encoded_keys")
def _():
    c.put_object(Bucket=LIST_BUCKET, Key="enc test/with space.txt", Body=b"x")
    r = c.list_objects_v2(Bucket=LIST_BUCKET, Prefix="enc test/", EncodingType="url")
    keys = [o["Key"] for o in r.get("Contents", [])]
    assert any("%20" in k or "%2F" in k.lower() or "%2f" in k for k in keys), keys


@test("list_v2_start_after")
def _():
    r = c.list_objects_v2(Bucket=LIST_BUCKET, StartAfter="c")
    keys = [o["Key"] for o in r.get("Contents", [])]
    assert "a" not in keys and "b" not in keys and "c" not in keys, keys


@test("list_v2_empty_bucket_no_contents")
def _():
    EB = f"v3empty-{uuid.uuid4().hex[:8]}"
    c.create_bucket(Bucket=EB)
    r = c.list_objects_v2(Bucket=EB)
    assert r.get("KeyCount", 0) == 0, r
    assert r.get("Contents", []) == []
    c.delete_bucket(Bucket=EB)


@test("list_v2_pagination_with_encoding_type")
def _():
    EB = f"v3pgenc-{uuid.uuid4().hex[:8]}"
    c.create_bucket(Bucket=EB)
    for i in range(15):
        c.put_object(Bucket=EB, Key=f"page/{i:03d}", Body=b"x")
    seen = []
    token = None
    while True:
        kw = {"Bucket": EB, "Prefix": "page/", "MaxKeys": 4, "EncodingType": "url"}
        if token:
            kw["ContinuationToken"] = token
        r = c.list_objects_v2(**kw)
        seen += [o["Key"] for o in r.get("Contents", [])]
        if not r.get("IsTruncated"):
            break
        token = r["NextContinuationToken"]
    assert len(seen) == 15, len(seen)


section("Range requests")

RANGE_BODY = b"The quick brown fox jumps over the lazy dog."
c.put_object(Bucket=B, Key="range.txt", Body=RANGE_BODY)


@test("range_first_5")
def _():
    g = c.get_object(Bucket=B, Key="range.txt", Range="bytes=0-4")
    assert g["Body"].read() == b"The q"
    assert g["ContentLength"] == 5
    assert g["ContentRange"] == f"bytes 0-4/{len(RANGE_BODY)}"


@test("range_single_byte")
def _():
    g = c.get_object(Bucket=B, Key="range.txt", Range="bytes=4-4")
    assert g["Body"].read() == b"q"


@test("range_suffix_minus10")
def _():
    g = c.get_object(Bucket=B, Key="range.txt", Range="bytes=-10")
    assert g["Body"].read() == RANGE_BODY[-10:]


@test("range_open_ended")
def _():
    g = c.get_object(Bucket=B, Key="range.txt", Range="bytes=40-")
    assert g["Body"].read() == RANGE_BODY[40:]


@test("range_beyond_returns_416")
def _():
    try:
        c.get_object(
            Bucket=B,
            Key="range.txt",
            Range=f"bytes={len(RANGE_BODY) + 10}-{len(RANGE_BODY) + 20}",
        )
        raise AssertionError("expected 416")
    except ClientError as e:
        assert e.response["ResponseMetadata"]["HTTPStatusCode"] == 416, e.response


@test("range_invalid_returns_400_or_416_or_ignored")
def _():
    try:
        c.get_object(Bucket=B, Key="range.txt", Range="bytes=badness")
    except ClientError as e:
        assert e.response["ResponseMetadata"]["HTTPStatusCode"] in (400, 416)


section("Conditional requests")

c.put_object(Bucket=B, Key="cond.txt", Body=b"cond")
etag = c.head_object(Bucket=B, Key="cond.txt")["ETag"]


@test("get_if_match_correct_etag_200")
def _():
    g = c.get_object(Bucket=B, Key="cond.txt", IfMatch=etag)
    assert g["Body"].read() == b"cond"


@test("get_if_match_wrong_etag_412")
def _():
    try:
        c.get_object(Bucket=B, Key="cond.txt", IfMatch='"deadbeef"')
        raise AssertionError("expected 412")
    except ClientError as e:
        assert e.response["ResponseMetadata"]["HTTPStatusCode"] == 412


@test("get_if_none_match_correct_etag_304")
def _():
    try:
        c.get_object(Bucket=B, Key="cond.txt", IfNoneMatch=etag)
        raise AssertionError("expected 304")
    except ClientError as e:
        assert e.response["ResponseMetadata"]["HTTPStatusCode"] == 304


@test("get_if_modified_since_old_200")
def _():
    g = c.get_object(
        Bucket=B,
        Key="cond.txt",
        IfModifiedSince=datetime.datetime(2000, 1, 1, tzinfo=datetime.timezone.utc),
    )
    assert g["Body"].read() == b"cond"


@test("get_if_modified_since_future_304")
def _():
    future = datetime.datetime(2099, 1, 1, tzinfo=datetime.timezone.utc)
    try:
        c.get_object(Bucket=B, Key="cond.txt", IfModifiedSince=future)
        raise AssertionError("expected 304")
    except ClientError as e:
        assert e.response["ResponseMetadata"]["HTTPStatusCode"] == 304


@test("get_if_unmodified_since_old_412")
def _():
    try:
        c.get_object(
            Bucket=B,
            Key="cond.txt",
            IfUnmodifiedSince=datetime.datetime(2000, 1, 1, tzinfo=datetime.timezone.utc),
        )
        raise AssertionError("expected 412")
    except ClientError as e:
        assert e.response["ResponseMetadata"]["HTTPStatusCode"] == 412


@test("put_if_none_match_star_412_when_exists")
def _():
    try:
        c.put_object(Bucket=B, Key="cond.txt", Body=b"new", IfNoneMatch="*")
        raise AssertionError("expected 412")
    except ClientError as e:
        assert e.response["Error"]["Code"] in ("PreconditionFailed", "412")


@test("put_if_match_etag")
def _():
    h = c.head_object(Bucket=B, Key="cond.txt")
    et = h["ETag"]
    c.put_object(Bucket=B, Key="cond.txt", Body=b"new2", IfMatch=et)
    assert c.get_object(Bucket=B, Key="cond.txt")["Body"].read() == b"new2"


@test("put_if_match_wrong_412")
def _():
    try:
        c.put_object(Bucket=B, Key="cond.txt", Body=b"x", IfMatch='"abc"')
        raise AssertionError("expected 412")
    except ClientError as e:
        assert e.response["Error"]["Code"] in ("PreconditionFailed", "412")


section("Multipart uploads")


@test("mpu_three_parts_5MiB_each")
def _():
    key = "mpu-3.bin"
    mpu = c.create_multipart_upload(Bucket=B, Key=key)
    uid = mpu["UploadId"]
    part_size = 5 * 1024 * 1024
    parts = []
    full = b""
    for i in range(1, 4):
        chunk = bytes([i]) * part_size
        full += chunk
        r = c.upload_part(Bucket=B, Key=key, UploadId=uid, PartNumber=i, Body=chunk)
        parts.append({"PartNumber": i, "ETag": r["ETag"]})
    c.complete_multipart_upload(
        Bucket=B, Key=key, UploadId=uid, MultipartUpload={"Parts": parts}
    )
    got = c.get_object(Bucket=B, Key=key)["Body"].read()
    assert len(got) == 15 * 1024 * 1024
    assert got == full


@test("mpu_out_of_order_parts_complete")
def _():
    key = "mpu-ooo.bin"
    mpu = c.create_multipart_upload(Bucket=B, Key=key)
    uid = mpu["UploadId"]
    chunks = [bytes([i]) * (5 * 1024 * 1024) for i in range(1, 4)]
    parts = {}
    for i in [3, 1, 2]:
        r = c.upload_part(Bucket=B, Key=key, UploadId=uid, PartNumber=i, Body=chunks[i - 1])
        parts[i] = r["ETag"]
    c.complete_multipart_upload(
        Bucket=B,
        Key=key,
        UploadId=uid,
        MultipartUpload={"Parts": [{"PartNumber": i, "ETag": parts[i]} for i in (1, 2, 3)]},
    )
    got = c.get_object(Bucket=B, Key=key)["Body"].read()
    assert got == b"".join(chunks)


@test("mpu_part_reupload_replaces")
def _():
    key = "mpu-re.bin"
    mpu = c.create_multipart_upload(Bucket=B, Key=key)
    uid = mpu["UploadId"]
    a = b"A" * (5 * 1024 * 1024)
    b2 = b"B" * (5 * 1024 * 1024)
    c.upload_part(Bucket=B, Key=key, UploadId=uid, PartNumber=1, Body=a)
    r = c.upload_part(Bucket=B, Key=key, UploadId=uid, PartNumber=1, Body=b2)
    c.complete_multipart_upload(
        Bucket=B,
        Key=key,
        UploadId=uid,
        MultipartUpload={"Parts": [{"PartNumber": 1, "ETag": r["ETag"]}]},
    )
    assert c.get_object(Bucket=B, Key=key)["Body"].read() == b2


@test("mpu_small_part_not_last_rejected")
def _():
    key = "mpu-small.bin"
    mpu = c.create_multipart_upload(Bucket=B, Key=key)
    uid = mpu["UploadId"]
    r1 = c.upload_part(Bucket=B, Key=key, UploadId=uid, PartNumber=1, Body=b"x" * 1024)
    r2 = c.upload_part(Bucket=B, Key=key, UploadId=uid, PartNumber=2, Body=b"y" * (5 * 1024 * 1024))
    try:
        c.complete_multipart_upload(
            Bucket=B,
            Key=key,
            UploadId=uid,
            MultipartUpload={
                "Parts": [
                    {"PartNumber": 1, "ETag": r1["ETag"]},
                    {"PartNumber": 2, "ETag": r2["ETag"]},
                ]
            },
        )
        raise AssertionError("expected EntityTooSmall")
    except ClientError as e:
        assert e.response["Error"]["Code"] in ("EntityTooSmall", "InvalidPart", "400"), e.response["Error"]
    c.abort_multipart_upload(Bucket=B, Key=key, UploadId=uid)


@test("mpu_list_uploads")
def _():
    key = "mpu-list.bin"
    mpu = c.create_multipart_upload(Bucket=B, Key=key)
    uid = mpu["UploadId"]
    r = c.list_multipart_uploads(Bucket=B)
    uploads = r.get("Uploads", [])
    found = [u for u in uploads if u["UploadId"] == uid]
    assert found, [u["UploadId"] for u in uploads]
    c.abort_multipart_upload(Bucket=B, Key=key, UploadId=uid)


@test("mpu_list_parts")
def _():
    key = "mpu-parts.bin"
    mpu = c.create_multipart_upload(Bucket=B, Key=key)
    uid = mpu["UploadId"]
    c.upload_part(Bucket=B, Key=key, UploadId=uid, PartNumber=1, Body=b"x" * (5 * 1024 * 1024))
    c.upload_part(Bucket=B, Key=key, UploadId=uid, PartNumber=2, Body=b"y" * (5 * 1024 * 1024))
    r = c.list_parts(Bucket=B, Key=key, UploadId=uid)
    pns = [p["PartNumber"] for p in r.get("Parts", [])]
    assert pns == [1, 2], pns
    c.abort_multipart_upload(Bucket=B, Key=key, UploadId=uid)


@test("mpu_abort_then_completing_fails")
def _():
    key = "mpu-abort.bin"
    mpu = c.create_multipart_upload(Bucket=B, Key=key)
    uid = mpu["UploadId"]
    r = c.upload_part(Bucket=B, Key=key, UploadId=uid, PartNumber=1, Body=b"x" * (5 * 1024 * 1024))
    c.abort_multipart_upload(Bucket=B, Key=key, UploadId=uid)
    try:
        c.complete_multipart_upload(
            Bucket=B,
            Key=key,
            UploadId=uid,
            MultipartUpload={"Parts": [{"PartNumber": 1, "ETag": r["ETag"]}]},
        )
        raise AssertionError("expected NoSuchUpload")
    except ClientError as e:
        assert e.response["Error"]["Code"] in ("NoSuchUpload", "404")


@test("mpu_complete_with_wrong_etag_fails")
def _():
    key = "mpu-bad-etag.bin"
    mpu = c.create_multipart_upload(Bucket=B, Key=key)
    uid = mpu["UploadId"]
    c.upload_part(Bucket=B, Key=key, UploadId=uid, PartNumber=1, Body=b"x" * (5 * 1024 * 1024))
    try:
        c.complete_multipart_upload(
            Bucket=B,
            Key=key,
            UploadId=uid,
            MultipartUpload={"Parts": [{"PartNumber": 1, "ETag": '"deadbeef"'}]},
        )
        raise AssertionError("expected InvalidPart")
    except ClientError as e:
        assert e.response["Error"]["Code"] in ("InvalidPart", "400")
    c.abort_multipart_upload(Bucket=B, Key=key, UploadId=uid)


section("Copy")


@test("copy_same_bucket")
def _():
    c.put_object(Bucket=B, Key="src.txt", Body=b"original")
    c.copy_object(Bucket=B, Key="copy.txt", CopySource={"Bucket": B, "Key": "src.txt"})
    assert c.get_object(Bucket=B, Key="copy.txt")["Body"].read() == b"original"


@test("copy_cross_bucket")
def _():
    B2 = f"v3copy-{uuid.uuid4().hex[:8]}"
    c.create_bucket(Bucket=B2)
    c.copy_object(Bucket=B2, Key="dst", CopySource={"Bucket": B, "Key": "src.txt"})
    assert c.get_object(Bucket=B2, Key="dst")["Body"].read() == b"original"


@test("copy_replace_metadata")
def _():
    c.copy_object(
        Bucket=B,
        Key="copy-meta.txt",
        CopySource={"Bucket": B, "Key": "src.txt"},
        Metadata={"x": "y"},
        MetadataDirective="REPLACE",
    )
    h = c.head_object(Bucket=B, Key="copy-meta.txt")
    meta = {k.lower(): v for k, v in h.get("Metadata", {}).items()}
    assert meta.get("x") == "y", meta


@test("copy_missing_source_404")
def _():
    try:
        c.copy_object(
            Bucket=B,
            Key="x",
            CopySource={"Bucket": B, "Key": "nope-" + uuid.uuid4().hex},
        )
        raise AssertionError("expected 404")
    except ClientError as e:
        assert e.response["ResponseMetadata"]["HTTPStatusCode"] in (404, 400)


@test("upload_part_copy")
def _():
    src_key = "upc-src.bin"
    body = os.urandom(6 * 1024 * 1024)
    c.put_object(Bucket=B, Key=src_key, Body=body)
    dst_key = "upc-dst.bin"
    mpu = c.create_multipart_upload(Bucket=B, Key=dst_key)
    uid = mpu["UploadId"]
    r = c.upload_part_copy(
        Bucket=B,
        Key=dst_key,
        UploadId=uid,
        PartNumber=1,
        CopySource={"Bucket": B, "Key": src_key},
    )
    etag = r["CopyPartResult"]["ETag"]
    c.complete_multipart_upload(
        Bucket=B,
        Key=dst_key,
        UploadId=uid,
        MultipartUpload={"Parts": [{"PartNumber": 1, "ETag": etag}]},
    )
    assert c.get_object(Bucket=B, Key=dst_key)["Body"].read() == body


section("Tagging")


@test("object_tags_put_get_delete")
def _():
    c.put_object(Bucket=B, Key="tagged.txt", Body=b"x")
    c.put_object_tagging(
        Bucket=B,
        Key="tagged.txt",
        Tagging={
            "TagSet": [
                {"Key": "env", "Value": "prod"},
                {"Key": "team", "Value": "core"},
            ]
        },
    )
    r = c.get_object_tagging(Bucket=B, Key="tagged.txt")
    tags = {t["Key"]: t["Value"] for t in r["TagSet"]}
    assert tags == {"env": "prod", "team": "core"}, tags
    c.delete_object_tagging(Bucket=B, Key="tagged.txt")
    r2 = c.get_object_tagging(Bucket=B, Key="tagged.txt")
    assert r2["TagSet"] == [], r2


@test("put_object_with_tagging_header")
def _():
    c.put_object(Bucket=B, Key="ptag.txt", Body=b"x", Tagging="a=1&b=2")
    r = c.get_object_tagging(Bucket=B, Key="ptag.txt")
    tags = {t["Key"]: t["Value"] for t in r["TagSet"]}
    assert tags == {"a": "1", "b": "2"}, tags


@test("tags_with_special_value_chars")
def _():
    c.put_object(Bucket=B, Key="tag-spec.txt", Body=b"x")
    c.put_object_tagging(
        Bucket=B,
        Key="tag-spec.txt",
        Tagging={"TagSet": [{"Key": "path", "Value": "a/b c"}]},
    )
    r = c.get_object_tagging(Bucket=B, Key="tag-spec.txt")
    tags = {t["Key"]: t["Value"] for t in r["TagSet"]}
    assert tags == {"path": "a/b c"}, tags


section("Versioning")

c.create_bucket(Bucket=BV)
c.put_bucket_versioning(Bucket=BV, VersioningConfiguration={"Status": "Enabled"})


@test("get_bucket_versioning_enabled")
def _():
    r = c.get_bucket_versioning(Bucket=BV)
    assert r.get("Status") == "Enabled", r


@test("multi_version_put_get_by_version")
def _():
    v1 = c.put_object(Bucket=BV, Key="v.txt", Body=b"a")["VersionId"]
    v2 = c.put_object(Bucket=BV, Key="v.txt", Body=b"b")["VersionId"]
    v3 = c.put_object(Bucket=BV, Key="v.txt", Body=b"c")["VersionId"]
    assert v1 and v2 and v3 and len({v1, v2, v3}) == 3
    assert c.get_object(Bucket=BV, Key="v.txt", VersionId=v1)["Body"].read() == b"a"
    assert c.get_object(Bucket=BV, Key="v.txt", VersionId=v2)["Body"].read() == b"b"
    assert c.get_object(Bucket=BV, Key="v.txt", VersionId=v3)["Body"].read() == b"c"
    assert c.get_object(Bucket=BV, Key="v.txt")["Body"].read() == b"c"


@test("delete_creates_delete_marker_in_versioned")
def _():
    c.put_object(Bucket=BV, Key="dm.txt", Body=b"x")
    c.delete_object(Bucket=BV, Key="dm.txt")
    try:
        c.get_object(Bucket=BV, Key="dm.txt")
        raise AssertionError("expected 404")
    except ClientError as e:
        assert e.response["ResponseMetadata"]["HTTPStatusCode"] == 404
    r = c.list_object_versions(Bucket=BV, Prefix="dm.txt")
    markers = r.get("DeleteMarkers", [])
    assert markers, r


@test("delete_specific_version_only_removes_that_version")
def _():
    v1 = c.put_object(Bucket=BV, Key="sd.txt", Body=b"v1")["VersionId"]
    c.put_object(Bucket=BV, Key="sd.txt", Body=b"v2")
    c.delete_object(Bucket=BV, Key="sd.txt", VersionId=v1)
    assert c.get_object(Bucket=BV, Key="sd.txt")["Body"].read() == b"v2"
    try:
        c.get_object(Bucket=BV, Key="sd.txt", VersionId=v1)
        raise AssertionError("v1 should be gone")
    except ClientError as e:
        assert e.response["ResponseMetadata"]["HTTPStatusCode"] == 404


@test("list_object_versions_returns_all")
def _():
    r = c.list_object_versions(Bucket=BV, Prefix="v.txt")
    vs = r.get("Versions", [])
    assert len(vs) >= 3, vs
    latest = [v for v in vs if v.get("IsLatest")]
    assert len(latest) == 1, latest


@test("suspend_versioning_then_put_overwrites")
def _():
    BSV = f"v3susp-{uuid.uuid4().hex[:8]}"
    c.create_bucket(Bucket=BSV)
    c.put_bucket_versioning(Bucket=BSV, VersioningConfiguration={"Status": "Enabled"})
    c.put_object(Bucket=BSV, Key="o", Body=b"v1")
    c.put_bucket_versioning(Bucket=BSV, VersioningConfiguration={"Status": "Suspended"})
    c.put_object(Bucket=BSV, Key="o", Body=b"v2")
    r = c.list_object_versions(Bucket=BSV, Prefix="o")
    assert len(r.get("Versions", [])) >= 1, r


section("Bulk delete")


@test("delete_objects_returns_deleted_list")
def _():
    BD = f"v3bd-{uuid.uuid4().hex[:8]}"
    c.create_bucket(Bucket=BD)
    for i in range(10):
        c.put_object(Bucket=BD, Key=f"k{i}", Body=b"x")
    r = c.delete_objects(
        Bucket=BD,
        Delete={"Objects": [{"Key": f"k{i}"} for i in range(10)], "Quiet": False},
    )
    assert len(r.get("Deleted", [])) == 10, r
    assert c.list_objects_v2(Bucket=BD).get("KeyCount", 0) == 0
    c.delete_bucket(Bucket=BD)


@test("delete_objects_quiet_mode_omits_successes")
def _():
    BD = f"v3bdq-{uuid.uuid4().hex[:8]}"
    c.create_bucket(Bucket=BD)
    for i in range(3):
        c.put_object(Bucket=BD, Key=f"k{i}", Body=b"x")
    r = c.delete_objects(
        Bucket=BD,
        Delete={"Objects": [{"Key": f"k{i}"} for i in range(3)], "Quiet": True},
    )
    assert len(r.get("Deleted", [])) == 0, r
    assert c.list_objects_v2(Bucket=BD).get("KeyCount", 0) == 0
    c.delete_bucket(Bucket=BD)


@test("delete_objects_mixed_existing_and_missing")
def _():
    BD = f"v3bdm-{uuid.uuid4().hex[:8]}"
    c.create_bucket(Bucket=BD)
    c.put_object(Bucket=BD, Key="exists", Body=b"x")
    r = c.delete_objects(
        Bucket=BD,
        Delete={
            "Objects": [
                {"Key": "exists"},
                {"Key": "missing-1"},
                {"Key": "missing-2"},
            ]
        },
    )
    deleted_keys = [d["Key"] for d in r.get("Deleted", [])]
    assert "exists" in deleted_keys, deleted_keys
    c.delete_bucket(Bucket=BD)


section("Checksums")


def _checksum_b64(data, algo):
    if algo == "SHA256":
        return base64.b64encode(hashlib.sha256(data).digest()).decode()
    if algo == "SHA1":
        return base64.b64encode(hashlib.sha1(data).digest()).decode()
    if algo == "CRC32":
        import zlib
        return base64.b64encode(zlib.crc32(data).to_bytes(4, "big")).decode()
    raise ValueError(algo)


@test("put_with_sha256_checksum")
def _():
    body = b"checksum-body"
    ck = _checksum_b64(body, "SHA256")
    c.put_object(Bucket=B, Key="ck-sha.txt", Body=body, ChecksumSHA256=ck)
    g = c.get_object(Bucket=B, Key="ck-sha.txt", ChecksumMode="ENABLED")
    assert g["Body"].read() == body


@test("put_with_wrong_sha256_rejected")
def _():
    try:
        c.put_object(
            Bucket=B,
            Key="bad-ck.txt",
            Body=b"x",
            ChecksumSHA256=_checksum_b64(b"different", "SHA256"),
        )
        raise AssertionError("expected BadDigest or InvalidChecksum")
    except ClientError as e:
        assert e.response["Error"]["Code"] in (
            "BadDigest",
            "InvalidRequest",
            "XAmzContentSHA256Mismatch",
            "400",
        )


@test("put_with_crc32_checksum")
def _():
    body = b"crc-body"
    ck = _checksum_b64(body, "CRC32")
    c.put_object(Bucket=B, Key="ck-crc.txt", Body=body, ChecksumCRC32=ck)
    g = c.get_object(Bucket=B, Key="ck-crc.txt")
    assert g["Body"].read() == body


@test("content_md5_correct_accepted")
def _():
    body = b"md5-body"
    md5_b64 = base64.b64encode(hashlib.md5(body).digest()).decode()
    c.put_object(Bucket=B, Key="md5-ok.txt", Body=body, ContentMD5=md5_b64)


@test("content_md5_wrong_rejected")
def _():
    try:
        c.put_object(
            Bucket=B,
            Key="md5-bad.txt",
            Body=b"x",
            ContentMD5=base64.b64encode(b"\x00" * 16).decode(),
        )
        raise AssertionError("expected BadDigest")
    except ClientError as e:
        assert e.response["Error"]["Code"] in ("BadDigest", "InvalidDigest", "400")


section("Presigned URLs")


@test("presigned_get")
def _():
    c.put_object(Bucket=B, Key="ps-get.txt", Body=b"presigned-get")
    url = c.generate_presigned_url(
        "get_object", Params={"Bucket": B, "Key": "ps-get.txt"}, ExpiresIn=60
    )
    with urllib.request.urlopen(url) as r:
        assert r.read() == b"presigned-get"


@test("presigned_put")
def _():
    url = c.generate_presigned_url(
        "put_object", Params={"Bucket": B, "Key": "ps-put.txt"}, ExpiresIn=60
    )
    req = urllib.request.Request(url, data=b"upload-via-presign", method="PUT")
    urllib.request.urlopen(req).read()
    assert c.get_object(Bucket=B, Key="ps-put.txt")["Body"].read() == b"upload-via-presign"


@test("presigned_head")
def _():
    url = c.generate_presigned_url(
        "head_object", Params={"Bucket": B, "Key": "ps-get.txt"}, ExpiresIn=60
    )
    req = urllib.request.Request(url, method="HEAD")
    with urllib.request.urlopen(req) as r:
        assert int(r.headers["Content-Length"]) == len(b"presigned-get")


@test("presigned_delete")
def _():
    c.put_object(Bucket=B, Key="ps-del.txt", Body=b"x")
    url = c.generate_presigned_url(
        "delete_object", Params={"Bucket": B, "Key": "ps-del.txt"}, ExpiresIn=60
    )
    req = urllib.request.Request(url, method="DELETE")
    urllib.request.urlopen(req).read()
    try:
        c.head_object(Bucket=B, Key="ps-del.txt")
        raise AssertionError("expected 404")
    except ClientError as e:
        assert e.response["ResponseMetadata"]["HTTPStatusCode"] == 404


@test("presigned_expired_rejected")
def _():
    url = c.generate_presigned_url(
        "get_object", Params={"Bucket": B, "Key": "ps-get.txt"}, ExpiresIn=1
    )
    time.sleep(2)
    try:
        with urllib.request.urlopen(url) as r:
            r.read()
        raise AssertionError("expected 403 expired")
    except urllib.error.HTTPError as e:
        assert e.code == 403, e.code


section("Auth and errors")


@test("bad_secret_returns_403")
def _():
    bad = boto3.client(
        "s3",
        endpoint_url=ENDPOINT,
        aws_access_key_id=AK,
        aws_secret_access_key="WRONG",
        region_name=REGION,
        config=Config(signature_version="s3v4", s3={"addressing_style": "path"}),
    )
    try:
        bad.list_buckets()
        raise AssertionError("expected 403")
    except ClientError as e:
        assert e.response["ResponseMetadata"]["HTTPStatusCode"] == 403
        assert e.response["Error"]["Code"] in ("SignatureDoesNotMatch", "403", "Forbidden")


@test("bad_access_key_returns_403")
def _():
    bad = boto3.client(
        "s3",
        endpoint_url=ENDPOINT,
        aws_access_key_id="WRONGKEY",
        aws_secret_access_key=SK,
        region_name=REGION,
        config=Config(signature_version="s3v4", s3={"addressing_style": "path"}),
    )
    try:
        bad.list_buckets()
        raise AssertionError("expected 403")
    except ClientError as e:
        assert e.response["ResponseMetadata"]["HTTPStatusCode"] == 403


@test("no_auth_header_returns_4xx")
def _():
    req = urllib.request.Request(ENDPOINT + "/")
    try:
        urllib.request.urlopen(req).read()
        raise AssertionError("expected 4xx")
    except urllib.error.HTTPError as e:
        assert e.code in (400, 403), e.code


@test("get_missing_key_returns_404")
def _():
    try:
        c.get_object(Bucket=B, Key="missing-" + uuid.uuid4().hex)
        raise AssertionError("expected 404")
    except ClientError as e:
        code = e.response["Error"]["Code"]
        assert code in ("NoSuchKey", "NotFound", "404"), code


@test("get_missing_bucket_returns_404")
def _():
    try:
        c.get_object(Bucket="never-" + uuid.uuid4().hex, Key="x")
        raise AssertionError("expected 404")
    except ClientError as e:
        code = e.response["Error"]["Code"]
        assert code in ("NoSuchBucket", "NotFound", "404"), code


@test("delete_non_empty_bucket_409")
def _():
    try:
        c.delete_bucket(Bucket=B)
        raise AssertionError("expected BucketNotEmpty")
    except ClientError as e:
        code = e.response["Error"]["Code"]
        assert "NotEmpty" in code or e.response["ResponseMetadata"]["HTTPStatusCode"] == 409


section("Concurrency")


@test("concurrent_puts_different_keys")
def _():
    BC = f"v3conc-{uuid.uuid4().hex[:8]}"
    c.create_bucket(Bucket=BC)
    def putk(i):
        c.put_object(Bucket=BC, Key=f"c{i}", Body=f"v{i}".encode())
    with ThreadPoolExecutor(max_workers=16) as ex:
        list(ex.map(putk, range(50)))
    r = c.list_objects_v2(Bucket=BC, MaxKeys=1000)
    keys = [o["Key"] for o in r.get("Contents", [])]
    assert len(keys) == 50, len(keys)
    c.delete_objects(Bucket=BC, Delete={"Objects": [{"Key": k} for k in keys]})
    c.delete_bucket(Bucket=BC)


@test("concurrent_puts_same_key_last_wins")
def _():
    key = "race"
    def putk(i):
        c.put_object(Bucket=B, Key=key, Body=f"writer-{i}".encode())
    with ThreadPoolExecutor(max_workers=16) as ex:
        list(ex.map(putk, range(50)))
    got = c.get_object(Bucket=B, Key=key)["Body"].read().decode()
    assert got.startswith("writer-"), got


@test("read_during_overwrite_no_corruption")
def _():
    key = "rd-conc"
    c.put_object(Bucket=B, Key=key, Body=b"A" * 1024)
    errors = []
    stop = threading.Event()
    def writer():
        for i in range(20):
            if stop.is_set():
                break
            payload = bytes([(i % 26) + ord("A")]) * 1024
            try:
                c.put_object(Bucket=B, Key=key, Body=payload)
            except Exception as e:
                errors.append(("w", e))
    def reader():
        for _ in range(40):
            try:
                data = c.get_object(Bucket=B, Key=key)["Body"].read()
                if len(data) != 1024:
                    errors.append(("size", len(data)))
                if data and data != bytes([data[0]]) * 1024:
                    errors.append(("torn", data[:20]))
            except Exception as e:
                errors.append(("r", e))
    ts = [
        threading.Thread(target=writer),
        threading.Thread(target=reader),
        threading.Thread(target=reader),
    ]
    [t.start() for t in ts]
    [t.join() for t in ts]
    stop.set()
    assert not errors, errors[:5]


section("Misc")


@test("zero_length_multipart_completes_or_rejects_cleanly")
def _():
    key = "mpu-zero.bin"
    mpu = c.create_multipart_upload(Bucket=B, Key=key)
    uid = mpu["UploadId"]
    r = c.upload_part(Bucket=B, Key=key, UploadId=uid, PartNumber=1, Body=b"")
    try:
        c.complete_multipart_upload(
            Bucket=B,
            Key=key,
            UploadId=uid,
            MultipartUpload={"Parts": [{"PartNumber": 1, "ETag": r["ETag"]}]},
        )
        assert c.get_object(Bucket=B, Key=key)["Body"].read() == b""
    except ClientError:
        c.abort_multipart_upload(Bucket=B, Key=key, UploadId=uid)


@test("head_after_delete_marker")
def _():
    c.put_object(Bucket=BV, Key="hdm.txt", Body=b"x")
    c.delete_object(Bucket=BV, Key="hdm.txt")
    try:
        c.head_object(Bucket=BV, Key="hdm.txt")
        raise AssertionError("expected 404")
    except ClientError as e:
        assert e.response["ResponseMetadata"]["HTTPStatusCode"] == 404


@test("etag_matches_md5_for_simple_put")
def _():
    body = b"etag-md5-test"
    r = c.put_object(Bucket=B, Key="etag.txt", Body=body)
    assert r["ETag"].strip('"') == md5(body), r["ETag"]


@test("etag_multipart_has_dash_partcount")
def _():
    key = "mpu-etag.bin"
    mpu = c.create_multipart_upload(Bucket=B, Key=key)
    uid = mpu["UploadId"]
    parts = []
    for i in range(1, 3):
        r = c.upload_part(Bucket=B, Key=key, UploadId=uid, PartNumber=i, Body=b"x" * (5 * 1024 * 1024))
        parts.append({"PartNumber": i, "ETag": r["ETag"]})
    r = c.complete_multipart_upload(
        Bucket=B, Key=key, UploadId=uid, MultipartUpload={"Parts": parts}
    )
    assert "-" in r["ETag"], r["ETag"]


print("\n" + "=" * 60)
print(f"PASSED: {len(PASS)}")
print(f"FAILED: {len(FAIL)}")
if FAIL:
    print("\nFailures:")
    for n, err in FAIL:
        print(f"  - {n}: {err}")
sys.exit(0 if not FAIL else 1)
