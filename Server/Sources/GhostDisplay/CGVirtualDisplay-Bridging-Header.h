//
//  CGVirtualDisplay-Bridging-Header.h
//  MacExtendServer
//
//  Reverse-engineered interface for CoreGraphics' private CGVirtualDisplay API
//  (the same private API used by Luna Display, DeskPad and BetterDisplay to create
//  ghost displays without a kernel/DriverKit driver). Not documented by Apple, so
//  these declarations may need to be adjusted if a future macOS release changes the
//  underlying implementation.
//

#import <Foundation/Foundation.h>
#import <CoreGraphics/CoreGraphics.h>

NS_ASSUME_NONNULL_BEGIN

typedef NS_ENUM(NSInteger, CGVirtualDisplayTerminationReason) {
    CGVirtualDisplayTerminationReasonUnknown = 0,
};

@interface CGVirtualDisplayMode : NSObject

- (instancetype)initWithWidth:(NSUInteger)width
                        height:(NSUInteger)height
                   refreshRate:(double)refreshRate;

@property (nonatomic, readonly) NSUInteger width;
@property (nonatomic, readonly) NSUInteger height;
@property (nonatomic, readonly) double refreshRate;

@end

@interface CGVirtualDisplaySettings : NSObject

@property (nonatomic, assign) NSUInteger hiDPI;
@property (nonatomic, copy) NSArray<CGVirtualDisplayMode *> *modes;

@end

@interface CGVirtualDisplayDescriptor : NSObject

@property (nonatomic, copy) NSString *name;
@property (nonatomic, assign) NSUInteger maxPixelsWide;
@property (nonatomic, assign) NSUInteger maxPixelsHigh;
@property (nonatomic, assign) CGSize sizeInMillimeters;
@property (nonatomic, assign) uint32_t productID;
@property (nonatomic, assign) uint32_t vendorID;
@property (nonatomic, assign) uint32_t serialNum;
@property (nonatomic, strong) dispatch_queue_t dispatchQueue;
@property (nonatomic, copy) void (^terminationHandler)(id display, CGVirtualDisplayTerminationReason reason);

@end

@interface CGVirtualDisplay : NSObject

- (nullable instancetype)initWithDescriptor:(CGVirtualDisplayDescriptor *)descriptor;
- (BOOL)applySettings:(CGVirtualDisplaySettings *)settings;

@property (nonatomic, readonly) uint32_t displayID;

@end

NS_ASSUME_NONNULL_END
