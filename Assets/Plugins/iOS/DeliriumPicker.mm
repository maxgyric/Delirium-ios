// The Files app's own picker for Delirium Sandbox (iOS):
//  - DP_PickFolder: a folder with your game's files, anywhere the Files app reaches (kept open across launches by a
//    bookmark: DP_RestoreFolder);
//  - DP_PickModels: .glb / .vrm files (and GMod maps, .bsp / .gma), MOVED into the app's Documents/mods (copied then the original removed when a
//    plain move isn't allowed there);
//  - DP_PickPackage: files (a VRChat .unitypackage; a .blend / .zip, with a textures .zip) copied into the app's tmp
//    folder for the import to read.
// Answers come back by UnitySendMessage to the "Game" object.
#import <UIKit/UIKit.h>

extern UIViewController* UnityGetGLViewController();
extern "C" void UnitySendMessage(const char* obj, const char* method, const char* msg);

static NSString* const kBookmark = @"delirium.folderBookmark";
static NSURL* gFolder = nil; // the picked folder, kept accessible

@interface DPDelegate : NSObject <UIDocumentPickerDelegate>
@property(nonatomic) BOOL folder;
@property(nonatomic) BOOL package;
@property(nonatomic, copy) NSString* obj;
@property(nonatomic, copy) NSString* method;
@end
static DPDelegate* gDelegate = nil;

static void DPSend(DPDelegate* d, NSString* msg) { UnitySendMessage(d.obj.UTF8String, d.method.UTF8String, msg.UTF8String); }

static void DPKeep(NSURL* u)
{
    if (gFolder) [gFolder stopAccessingSecurityScopedResource];
    [u startAccessingSecurityScopedResource];
    gFolder = u;
}

@implementation DPDelegate
- (void)documentPicker:(UIDocumentPickerViewController*)c didPickDocumentsAtURLs:(NSArray<NSURL*>*)urls
{
    NSFileManager* fm = [NSFileManager defaultManager];
    if (self.package) // several: a model and its textures' .zip (one path a line)
    {
        NSMutableArray* paths = [NSMutableArray array];
        for (NSURL* u in urls) [paths addObject:u.path];
        return DPSend(self, [paths componentsJoinedByString:@"\n"]);
    }
    if (self.folder)
    {
        NSURL* u = urls.firstObject;
        if (!u) return DPSend(self, @"");
        DPKeep(u);
        NSError* e = nil;
        NSData* b = [u bookmarkDataWithOptions:0 includingResourceValuesForKeys:nil relativeToURL:nil error:&e];
        if (b) [[NSUserDefaults standardUserDefaults] setObject:b forKey:kBookmark];
        return DPSend(self, u.path);
    }
    NSString* docs = NSSearchPathForDirectoriesInDomains(NSDocumentDirectory, NSUserDomainMask, YES).firstObject;
    NSString* mods = [docs stringByAppendingPathComponent:@"mods"];
    [fm createDirectoryAtPath:mods withIntermediateDirectories:YES attributes:nil error:nil];
    int moved = 0;
    NSMutableArray* notes = [NSMutableArray array];
    for (NSURL* u in urls)
    {
        NSString* ext = u.pathExtension.lowercaseString;
        if (![ext isEqualToString:@"glb"] && ![ext isEqualToString:@"vrm"] && ![ext isEqualToString:@"bsp"] && ![ext isEqualToString:@"gma"])
        {
            [notes addObject:[u.lastPathComponent stringByAppendingString:@" isn't a .glb, .vrm, .bsp or .gma"]];
            continue;
        }
        if ([u.path hasPrefix:mods]) continue; // already there
        BOOL scoped = [u startAccessingSecurityScopedResource];
        NSString* dest = [mods stringByAppendingPathComponent:u.lastPathComponent];
        for (int n = 2; [fm fileExistsAtPath:dest]; ++n)
            dest = [mods stringByAppendingPathComponent:[NSString stringWithFormat:@"%@ %d.%@", u.lastPathComponent.stringByDeletingPathExtension, n, u.pathExtension]];
        NSURL* to = [NSURL fileURLWithPath:dest];
        __block BOOL ok = NO, copiedOnly = NO;
        __block NSError* err = nil;
        NSError* coordErr = nil;
        NSFileCoordinator* fc = [[NSFileCoordinator alloc] initWithFilePresenter:nil];
        [fc coordinateWritingItemAtURL:u options:NSFileCoordinatorWritingForMoving writingItemAtURL:to options:NSFileCoordinatorWritingForReplacing
                                 error:&coordErr byAccessor:^(NSURL* from, NSURL* into) {
                                     ok = [fm moveItemAtURL:from toURL:into error:&err];
                                     if (ok) return;
                                     NSError* copyErr = nil;
                                     if (![fm copyItemAtURL:from toURL:into error:&copyErr]) { err = copyErr; return; }
                                     ok = YES;
                                     NSError* removeErr = nil;
                                     if (![fm removeItemAtURL:from error:&removeErr]) copiedOnly = YES;
                                 }];
        if (scoped) [u stopAccessingSecurityScopedResource];
        if (ok)
        {
            ++moved;
            if (copiedOnly) [notes addObject:[u.lastPathComponent stringByAppendingString:@" copied (its old place won't let it be removed)"]];
        }
        else
            [notes addObject:[NSString stringWithFormat:@"%@: %@", u.lastPathComponent, (err ?: coordErr).localizedDescription ?: @"didn't move"]];
    }
    DPSend(self, [NSString stringWithFormat:@"%d|%@", moved, [notes componentsJoinedByString:@"; "]]);
}

- (void)documentPickerWasCancelled:(UIDocumentPickerViewController*)c
{
    DPSend(self, self.folder || self.package ? @"" : @"-1|");
}
@end

static void DPPresent(BOOL folder, BOOL package, const char* obj, const char* method)
{
    if (!gDelegate) gDelegate = [DPDelegate new];
    gDelegate.folder = folder;
    gDelegate.package = package;
    gDelegate.obj = [NSString stringWithUTF8String:obj];
    gDelegate.method = [NSString stringWithUTF8String:method];
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
    UIDocumentPickerViewController* p =
        [[UIDocumentPickerViewController alloc] initWithDocumentTypes:folder ? @[ @"public.folder" ] : @[ @"public.item" ]
                                                               inMode:package ? UIDocumentPickerModeImport : UIDocumentPickerModeOpen];
#pragma clang diagnostic pop
    p.delegate = gDelegate;
    p.allowsMultipleSelection = !folder;
    p.modalPresentationStyle = UIModalPresentationFormSheet;
    [UnityGetGLViewController() presentViewController:p animated:YES completion:nil];
}

extern "C"
{
    void DP_PickFolder(const char* obj, const char* method) { DPPresent(YES, NO, obj, method); }
    void DP_PickModels(const char* obj, const char* method) { DPPresent(NO, NO, obj, method); }
    void DP_PickPackage(const char* obj, const char* method) { DPPresent(NO, YES, obj, method); }

    // the folder picked before (another launch), opened again; "" if none
    const char* DP_RestoreFolder()
    {
        NSData* b = [[NSUserDefaults standardUserDefaults] objectForKey:kBookmark];
        if (!b) return strdup("");
        BOOL stale = NO;
        NSError* e = nil;
        NSURL* u = [NSURL URLByResolvingBookmarkData:b options:0 relativeToURL:nil bookmarkDataIsStale:&stale error:&e];
        if (!u) return strdup("");
        DPKeep(u);
        if (stale)
        {
            NSData* nb = [u bookmarkDataWithOptions:0 includingResourceValuesForKeys:nil relativeToURL:nil error:nil];
            if (nb) [[NSUserDefaults standardUserDefaults] setObject:nb forKey:kBookmark];
        }
        return strdup(u.path.UTF8String);
    }
}
